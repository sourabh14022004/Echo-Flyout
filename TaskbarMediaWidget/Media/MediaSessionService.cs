using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using TaskbarMediaWidget.Settings;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarMediaWidget.Media;

/// <summary>
/// Wraps the Windows System Media Transport Controls (GSMTC) session manager: the real,
/// event-driven source of "what's currently playing" and the only way to control it globally.
/// Events fire on whatever thread WinRT calls back on — callers (UI code) are responsible for
/// marshaling to their Dispatcher.
/// </summary>
public sealed class MediaSessionService : IAsyncDisposable
{
    /// <summary>Fires on every properties/playback update, including no-op refreshes of the same track.</summary>
    public event EventHandler<MediaSnapshot>? MediaChanged;

    /// <summary>Fires only when the active track actually changes (new title/artist or a new session) — drives the next-track flyout.</summary>
    public event EventHandler<MediaSnapshot>? TrackChanged;

    /// <summary>The most recently published media snapshot.</summary>
    public MediaSnapshot CurrentSnapshot => _lastSnapshot;

    private readonly AppSettings _settings;

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private MediaSnapshot _lastSnapshot = MediaSnapshot.Idle;
    private bool _hasPublishedOnce;

    public MediaSessionService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        Logger.Log("MediaSessionService: requesting GSMTC session manager…");
        _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        Logger.Log("MediaSessionService: session manager acquired.");
        _sessionManager.CurrentSessionChanged += (_, _) => AttachToCurrentSession();
        AttachToCurrentSession();
    }

    public Task SkipPreviousAsync() => _currentSession?.TrySkipPreviousAsync().AsTask() ?? Task.CompletedTask;
    public Task TogglePlayPauseAsync() => _currentSession?.TryTogglePlayPauseAsync().AsTask() ?? Task.CompletedTask;
    public Task SkipNextAsync() => _currentSession?.TrySkipNextAsync().AsTask() ?? Task.CompletedTask;

    private void AttachToCurrentSession()
    {
        GlobalSystemMediaTransportControlsSession? session = _sessionManager?.GetCurrentSession();

        if (session == null)
        {
            Detach();
            Logger.Log("MediaSessionService: no current session.");
            Publish(MediaSnapshot.Idle);
            return;
        }

        AttachTo(session);
        _ = RefreshMediaPropertiesAsync();
    }

    private void AttachTo(GlobalSystemMediaTransportControlsSession session)
    {
        Detach();

        Logger.Log($"MediaSessionService: attached to session '{session.SourceAppUserModelId}'.");
        _currentSession = session;
        _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
    }

    private void Detach()
    {
        if (_currentSession == null) return;

        _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _currentSession = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshMediaPropertiesAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => RefreshPlaybackInfoOnly();

    private async Task RefreshMediaPropertiesAsync()
    {
        if (_currentSession == null) return;

        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties props =
                await _currentSession.TryGetMediaPropertiesAsync();

            GlobalSystemMediaTransportControlsSession session = _currentSession;

            if (RejectionReason(session, props) is string reason)
            {
                Logger.Log($"MediaSessionService: ignoring session '{session.SourceAppUserModelId}' Title='{props.Title}' — {reason}");

                // Windows nominated this session as "current", but it doesn't look like something
                // the user chose to play. Rather than showing it, look for a real one that's
                // playing elsewhere — so a web ad can't push Spotify off the widget.
                if (!await TryAttachToBetterSessionAsync(session))
                {
                    Publish(MediaSnapshot.Idle);
                }
                return;
            }

            await PublishSnapshotAsync(session, props);
        }
        catch (Exception ex)
        {
            Logger.LogException("MediaSessionService: RefreshMediaPropertiesAsync failed", ex);
        }
    }

    private async Task PublishSnapshotAsync(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        BitmapImage? thumbnail = null;
        if (props.Thumbnail != null)
        {
            thumbnail = await TryLoadThumbnailAsync(props.Thumbnail);
        }

        bool isPlaying = session.GetPlaybackInfo()?.PlaybackStatus
            == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var snapshot = new MediaSnapshot(
            HasSession: true,
            Title: props.Title,
            Artist: props.Artist ?? string.Empty,
            Album: props.AlbumTitle ?? string.Empty,
            Thumbnail: thumbnail,
            IsPlaying: isPlaying,
            SourceAppId: session.SourceAppUserModelId);

        Logger.Log($"MediaSessionService: publishing snapshot Title='{snapshot.Title}' Artist='{snapshot.Artist}' IsPlaying={snapshot.IsPlaying} HasThumbnail={thumbnail != null}");
        Publish(snapshot);
    }

    /// <summary>
    /// Why this session shouldn't be shown, or null if it's fine.
    ///
    /// Windows has no notion of "this is an advert" — a browser registers an autoplaying ad
    /// exactly the same way it registers a song, so there is no authoritative signal to read.
    /// What separates them in practice is metadata: something you chose to play almost always
    /// carries an artist or cover art (music, podcasts, YouTube all do), whereas an ad or a stray
    /// autoplaying clip usually carries neither, and often has no title at all.
    /// </summary>
    private string? RejectionReason(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        // A session with no title isn't something we can meaningfully display, whatever it is.
        if (string.IsNullOrWhiteSpace(props.Title))
        {
            return "no title";
        }

        if (_settings.FilterIncidentalAudio
            && string.IsNullOrWhiteSpace(props.Artist)
            && props.Thumbnail == null)
        {
            return "no artist and no artwork — looks like incidental audio";
        }

        if (_settings.MinimumTrackSeconds > 0)
        {
            TimeSpan length = TrackLength(session);

            // A zero length means the source didn't report one (live streams, some browsers).
            // Unknown is not the same as short, so it gets the benefit of the doubt.
            if (length > TimeSpan.Zero && length.TotalSeconds < _settings.MinimumTrackSeconds)
            {
                return $"only {length.TotalSeconds:F0}s long (under the {_settings.MinimumTrackSeconds}s minimum)";
            }
        }

        return null;
    }

    private static TimeSpan TrackLength(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            return timeline.EndTime - timeline.StartTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Looks past the rejected session for another one that is actually playing and does look
    /// like real media, and attaches to it instead. Returns false when there's nothing better.
    /// </summary>
    private async Task<bool> TryAttachToBetterSessionAsync(GlobalSystemMediaTransportControlsSession rejected)
    {
        if (_sessionManager == null) return false;

        foreach (GlobalSystemMediaTransportControlsSession candidate in _sessionManager.GetSessions())
        {
            if (candidate.SourceAppUserModelId == rejected.SourceAppUserModelId) continue;

            if (candidate.GetPlaybackInfo()?.PlaybackStatus
                != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                continue;
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties props =
                    await candidate.TryGetMediaPropertiesAsync();

                if (RejectionReason(candidate, props) != null) continue;

                Logger.Log($"MediaSessionService: switching to '{candidate.SourceAppUserModelId}' instead.");

                // Publish straight from the properties already in hand rather than kicking off
                // another refresh — that would re-enter this method and two rejected sessions
                // could bounce between each other forever.
                AttachTo(candidate);
                await PublishSnapshotAsync(candidate, props);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogException("MediaSessionService: could not inspect a candidate session", ex);
            }
        }

        return false;
    }

    private void RefreshPlaybackInfoOnly()
    {
        if (_currentSession == null) return;

        bool isPlaying = _currentSession.GetPlaybackInfo()?.PlaybackStatus
            == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        Publish(_lastSnapshot with { IsPlaying = isPlaying, HasSession = true });
    }

    private void Publish(MediaSnapshot snapshot)
    {
        // Suppress a TrackChanged on the very first snapshot ever seen (app startup finding
        // whatever's already playing) — the flyout is for actual transitions, not app launch.
        bool isTrackChange = _hasPublishedOnce && !snapshot.IsSameTrack(_lastSnapshot);
        _lastSnapshot = snapshot;
        _hasPublishedOnce = true;

        MediaChanged?.Invoke(this, snapshot);
        if (isTrackChange)
        {
            TrackChanged?.Invoke(this, snapshot);
        }
    }

    private static async Task<BitmapImage?> TryLoadThumbnailAsync(IRandomAccessStreamReference thumbnailRef)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await thumbnailRef.OpenReadAsync();
            using var reader = new DataReader(stream);
            uint size = (uint)stream.Size;
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);

            using var memoryStream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        return ValueTask.CompletedTask;
    }
}
