using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
// UseWindowsForms (needed for the tray icon) puts same-named types in scope, so pin these to WPF.
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace TaskbarMediaWidget.Widget;

/// <summary>
/// The "nothing is playing" banner: a small mascot that wanders the taskbar strip, hops, sparks
/// and occasionally chirps.
///
/// Everything is driven by WPF animations applied straight to render transforms via
/// <see cref="Animatable.BeginAnimation(DependencyProperty, AnimationTimeline)"/> — no
/// CompositionTarget.Rendering loop, no per-frame C#, and nothing that touches layout properties
/// (which would force a measure/arrange pass every frame). <see cref="Stop"/> tears every
/// animation down, so a hidden banner costs nothing in a widget that runs all day.
/// </summary>
public partial class IdleBanner : UserControl
{
    private const double StageWidth = 300;
    private const double SpriteWidth = 30;

    // Half the default 60fps: still smooth for a 30px sprite, roughly half the animation cost.
    private const int AnimationFrameRate = 30;

    private static readonly string[] Chirps =
    {
        "pika!", "pika pika!", "pikachu!", "pi-ka-chu!", "chuuu~", "pika?"
    };

    private readonly Random _random = new();
    private readonly DispatcherTimer _scheduler;

    private bool _isRunning;
    private int _lastBehaviour = -1;
    private int _repeatCount;

    public IdleBanner()
    {
        InitializeComponent();

        _scheduler = new DispatcherTimer(DispatcherPriority.Background);
        _scheduler.Tick += (_, _) => RunNextBehaviour();
    }

    /// <summary>Begins the ambient loop, walks the mascot on stage, and starts the behaviour scheduler.</summary>
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        StartAmbientLoops();
        EnterStage();
        ScheduleNextBehaviour();
    }

    /// <summary>Stops the scheduler and removes every running animation.</summary>
    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        _scheduler.Stop();
        ClearAllAnimations();
    }

    // ----- Scheduling -----

    private void ScheduleNextBehaviour()
    {
        _scheduler.Interval = TimeSpan.FromMilliseconds(_random.Next(2200, 4500));
        _scheduler.Start();
    }

    private void RunNextBehaviour()
    {
        _scheduler.Stop();
        if (!_isRunning) return;

        switch (PickBehaviour())
        {
            case 0: Walk(); break;
            case 1: Hop(); break;
            case 2: Speak(); break;
            case 3: Spark(); break;
            default: BlinkAndMaybeTurn(); break;
        }

        ScheduleNextBehaviour();
    }

    /// <summary>Weighted pick (walk 35%, hop 15%, speak 20%, spark 15%, blink 15%) that avoids long repeats.</summary>
    private int PickBehaviour()
    {
        int candidate = Roll();
        if (candidate == _lastBehaviour && _repeatCount >= 1)
        {
            candidate = Roll(); // one re-roll is enough to break up runs of the same behaviour
        }

        _repeatCount = candidate == _lastBehaviour ? _repeatCount + 1 : 0;
        _lastBehaviour = candidate;
        return candidate;

        int Roll()
        {
            int roll = _random.Next(100);
            return roll < 35 ? 0
                 : roll < 50 ? 1
                 : roll < 70 ? 2
                 : roll < 85 ? 3
                 : 4;
        }
    }

    // ----- Behaviours -----

    private void StartAmbientLoops()
    {
        Animate(SpriteBreath, ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.03, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });

        Animate(TailRotate, RotateTransform.AngleProperty,
            new DoubleAnimation(-8, 8, TimeSpan.FromSeconds(1.1))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void EnterStage()
    {
        Animate(SpriteFacing, ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, TimeSpan.FromSeconds(0.01)));

        Animate(SpriteTranslate, TranslateTransform.XProperty,
            new DoubleAnimation(-34, 24, TimeSpan.FromSeconds(0.7))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });

        StepBob(TimeSpan.FromSeconds(0.7));
    }

    private void Walk()
    {
        double current = SpriteTranslate.X;
        double target = _random.NextDouble() * (StageWidth - SpriteWidth);
        double distance = Math.Abs(target - current);
        var duration = TimeSpan.FromSeconds(Math.Max(0.4, distance / 55.0));

        Animate(SpriteFacing, ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target < current ? -1 : 1, TimeSpan.FromSeconds(0.12)));

        Animate(SpriteTranslate, TranslateTransform.XProperty,
            new DoubleAnimation(target, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

        StepBob(duration);

        // FillBehavior.Stop returns the ears to their resting 0° when the walk ends.
        var sway = new DoubleAnimation(-6, 6, TimeSpan.FromSeconds(0.48))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(duration),
            FillBehavior = FillBehavior.Stop
        };
        Animate(EarLeftRotate, RotateTransform.AngleProperty, sway);
        Animate(EarRightRotate, RotateTransform.AngleProperty, sway.Clone());
    }

    private void StepBob(TimeSpan duration)
    {
        Animate(SpriteTranslate, TranslateTransform.YProperty,
            new DoubleAnimation(0, -1.5, TimeSpan.FromSeconds(0.24))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(duration),
                FillBehavior = FillBehavior.Stop
            });
    }

    private void Hop()
    {
        var height = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        height.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        height.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.28)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        height.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.55)),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        Animate(SpriteTranslate, TranslateTransform.YProperty, height);

        // Squash on the crouch, stretch on the launch, settle on landing.
        Animate(SpriteSquash, ScaleTransform.ScaleYProperty,
            KeyFrames((1, 0), (0.85, 0.08), (1.12, 0.2), (1, 0.42)));
        Animate(SpriteSquash, ScaleTransform.ScaleXProperty,
            KeyFrames((1, 0), (1.12, 0.08), (0.92, 0.2), (1, 0.42)));

        // Ears follow through a beat later.
        var ears = KeyFrames((0, 0), (14, 0.16), (-6, 0.34), (0, 0.5));
        ears.BeginTime = TimeSpan.FromMilliseconds(40);
        Animate(EarLeftRotate, RotateTransform.AngleProperty, ears);
        Animate(EarRightRotate, RotateTransform.AngleProperty, ears.Clone());
    }

    private void Speak()
    {
        BubbleText.Text = Chirps[_random.Next(Chirps.Length)];

        // One measure pass (not per frame) to place the bubble beside the mascot.
        Bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bubbleWidth = Bubble.DesiredSize.Width;

        double spriteX = SpriteTranslate.X;
        double x = spriteX < StageWidth * 0.55
            ? spriteX + 26                      // bubble to the right
            : spriteX + 4 - bubbleWidth;        // ...flipped to the left near the far end
        BubbleTranslate.X = Math.Clamp(x, 0, Math.Max(0, StageWidth - bubbleWidth));

        Animate(Bubble, OpacityProperty, KeyFrames((0, 0), (1, 0.18), (1, 1.35), (0, 1.55)));

        var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.22)),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }));
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.55))));
        Animate(BubbleScale, ScaleTransform.ScaleXProperty, pop);
        Animate(BubbleScale, ScaleTransform.ScaleYProperty, pop.Clone());

        Animate(MouthScale, ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 3, TimeSpan.FromSeconds(0.16))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(4),
                FillBehavior = FillBehavior.Stop
            });

        var perk = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(0.2))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(TimeSpan.FromSeconds(1.4)),
            FillBehavior = FillBehavior.Stop
        };
        Animate(EarLeftRotate, RotateTransform.AngleProperty, perk);
        Animate(EarRightRotate, RotateTransform.AngleProperty, perk.Clone());
    }

    private void Spark()
    {
        var glow = new DoubleAnimation(0, 0.9, TimeSpan.FromSeconds(0.12))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = FillBehavior.Stop
        };
        Animate(CheekGlowLeft, OpacityProperty, glow);
        Animate(CheekGlowRight, OpacityProperty, glow.Clone());

        (UIElement Bolt, ScaleTransform Scale)[] sparks =
        {
            (Spark0, Spark0Scale),
            (Spark1, Spark1Scale),
            (Spark2, Spark2Scale),
            (Spark3, Spark3Scale)
        };

        for (int i = 0; i < sparks.Length; i++)
        {
            var begin = TimeSpan.FromMilliseconds(i * 70);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.11))
            {
                BeginTime = begin,
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop
            };
            Animate(sparks[i].Bolt, OpacityProperty, fade);

            var pop = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.11))
            {
                BeginTime = begin,
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop
            };
            Animate(sparks[i].Scale, ScaleTransform.ScaleXProperty, pop);
            Animate(sparks[i].Scale, ScaleTransform.ScaleYProperty, pop.Clone());
        }

        // Shake around wherever the mascot currently stands. Key-framed back to that exact spot
        // rather than using FillBehavior.Stop, which would snap it home to the base value of 0.
        double baseX = SpriteTranslate.X;
        var shake = new DoubleAnimationUsingKeyFrames();
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(baseX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        for (int i = 1; i <= 5; i++)
        {
            double offset = i % 2 == 0 ? 1.5 : -1.5;
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(baseX + offset,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.04 * i))));
        }
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(baseX, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.26))));
        Animate(SpriteTranslate, TranslateTransform.XProperty, shake);
    }

    private void BlinkAndMaybeTurn()
    {
        var blink = KeyFrames((1, 0), (0.1, 0.07), (1, 0.15));
        Animate(EyeLeftScale, ScaleTransform.ScaleYProperty, blink);
        Animate(EyeRightScale, ScaleTransform.ScaleYProperty, blink.Clone());

        if (_random.NextDouble() >= 0.4) return;

        // Scaling through zero reads as a turn rather than an instant flip. HoldEnd (the default)
        // keeps the new facing after the animation finishes.
        double facing = SpriteFacing.ScaleX >= 0 ? 1 : -1;
        var turn = new DoubleAnimationUsingKeyFrames();
        turn.KeyFrames.Add(new LinearDoubleKeyFrame(facing, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        turn.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.06))));
        turn.KeyFrames.Add(new LinearDoubleKeyFrame(-facing, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.12))));
        Animate(SpriteFacing, ScaleTransform.ScaleXProperty, turn);
    }

    // ----- Animation plumbing -----

    private static void Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        Timeline.SetDesiredFrameRate(animation, AnimationFrameRate);
        target.BeginAnimation(property, animation);
    }

    /// <summary>Builds a linear key-framed animation from (value, seconds) pairs; stops (reverts to base) when done.</summary>
    private static DoubleAnimationUsingKeyFrames KeyFrames(params (double Value, double Seconds)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach ((double value, double seconds) in frames)
        {
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(value,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        }
        return animation;
    }

    private void ClearAllAnimations()
    {
        SpriteBreath.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SpriteSquash.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SpriteSquash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SpriteFacing.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SpriteTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        SpriteTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        TailRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        EarLeftRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        EarRightRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        EyeLeftScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EyeRightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MouthScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        CheekGlowLeft.BeginAnimation(OpacityProperty, null);
        CheekGlowRight.BeginAnimation(OpacityProperty, null);

        foreach ((UIElement bolt, ScaleTransform scale) in new (UIElement, ScaleTransform)[]
                 { (Spark0, Spark0Scale), (Spark1, Spark1Scale), (Spark2, Spark2Scale), (Spark3, Spark3Scale) })
        {
            bolt.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        Bubble.BeginAnimation(OpacityProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
}
