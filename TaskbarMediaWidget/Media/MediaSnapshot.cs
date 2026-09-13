using System.Windows.Media.Imaging;

namespace TaskbarMediaWidget.Media;

public sealed record MediaSnapshot(
    bool HasSession,
    string Title,
    string Artist,
    string Album,
    BitmapImage? Thumbnail,
    bool IsPlaying,
    string? SourceAppId)
{
    public static readonly MediaSnapshot Idle = new(false, "No media playing", string.Empty, string.Empty, null, false, null);

    /// <summary>Same logical track as another snapshot — used to distinguish a genuine track change from a property refresh.</summary>
    public bool IsSameTrack(MediaSnapshot other) =>
        HasSession == other.HasSession && Title == other.Title && Artist == other.Artist && SourceAppId == other.SourceAppId;
}
