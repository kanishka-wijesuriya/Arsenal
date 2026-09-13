using Arsenal.Helpers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.Views.Windows;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Action<ToastWindow> _onClosed;
    private readonly int _durationMs;
    private bool _closing;
    private bool _opened;
    private bool _hasPosition;
    private bool _liveMode;
    private int _liveSettleMs = 1000;
    private double _preparedHeight;

    // Countdown state, so hovering pauses rather than restarting it.
    private int _remainingMs;
    private DateTime _countdownResumedAt;

    // Which way the toast travels. Derived from where it sits, so it always enters
    // from, and leaves towards, the edge it is anchored to - a top-centre toast that
    // flew in from the right read as if it belonged somewhere else.
    private readonly double _enterX;
    private readonly double _enterY;

    private const double EnterDistance = 42;
    private const double ExitDistance = 36;

    /// <summary>
    /// Transparent padding around the visible card, matching MotionRoot's margin. The
    /// slide animation moves the content within it instead of clipping at the window
    /// edge; callers positioning the window must discount it.
    /// </summary>
    public const double Slack = 48;
    public const double VerticalSlack = 128;

    private const int AnimationFrameRate = 120;

    /// <summary>Height of the visible card, excluding the slack on both sides.</summary>
    public double VisibleHeight => Math.Max(0,
        (ActualHeight > 0 ? ActualHeight : _preparedHeight) - (VerticalSlack * 2));

    /// <summary>
    /// Width of the visible card, excluding the slack on both sides. Falls back to the
    /// declared width before the HWND exists: ActualWidth is 0 until then, and a card
    /// measured as zero-width centres on its own left edge, which the real measurement
    /// afterwards then has to travel half a card to correct.
    /// </summary>
    public double VisibleWidth => Math.Max(0, (ActualWidth > 0 ? ActualWidth : Width) - (Slack * 2));

    public bool IsClosing => _closing;

    public event Action<ToastWindow>? ClosingStarted;

    public string Message { get; }
    public string Detail { get; }
    public SymbolRegular IconSymbol { get; }

    public ToastWindow(string message, ToastIcon icon, Action<ToastWindow> onClosed, string? detail = null)
    {
        Message = message;
        Detail = string.IsNullOrWhiteSpace(detail) ? DescribeSource(icon) : detail;
        IconSymbol = icon switch
        {
            ToastIcon.BrightnessUp => SymbolRegular.BrightnessHigh24,
            ToastIcon.BrightnessDown => SymbolRegular.BrightnessLow24,
            ToastIcon.BacklightUp => SymbolRegular.KeyboardShiftUppercase24,
            ToastIcon.BacklightDown => SymbolRegular.Keyboard24,
            ToastIcon.Touchpad => SymbolRegular.KeyboardMouse16,
            ToastIcon.Microphone => SymbolRegular.Mic24,
            ToastIcon.MicrophoneMute => SymbolRegular.MicOff24,
            ToastIcon.Battery => SymbolRegular.BatterySaver24,
            ToastIcon.Charger => SymbolRegular.PlugConnected24,
            ToastIcon.FnLock => SymbolRegular.Keyboard24,
            ToastIcon.Controller => SymbolRegular.XboxController24,
            _ => SymbolRegular.Info24
        };
        _onClosed = onClosed;
        _durationMs = Math.Clamp(AppConfig.Get("toast_duration", 3500), 1500, 12000);

        // 0 top right, 1 bottom right, 2 top centre, 3 bottom centre. The right-hand
        // positions hug the right edge, so they keep travelling horizontally; the
        // centred ones have no side to come from and use the nearer horizontal edge.
        int position = Math.Clamp(AppConfig.Get("toast_position", 0), 0, 3);
        bool centred = position >= 2;
        bool bottom = position is 1 or 3;

        _enterX = centred ? 0 : EnterDistance;
        _enterY = centred ? (bottom ? EnterDistance : -EnterDistance) : 0;

        InitializeComponent();
        // Keep the native layered window fully transparent until ToastManager has
        // measured and positioned it. Otherwise DWM can composite one frame at the
        // default window location before the stack layout runs, which looks like a
        // faint duplicate elsewhere on the desktop.
        Opacity = 0;
        MotionRoot.Opacity = 0;
        DataContext = this;
        ApplyDesign(AppConfig.Get("toast_style", 0));
        ProgressTrack.Visibility = AppConfig.IsNotFalse("toast_progress") ? Visibility.Visible : Visibility.Collapsed;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_durationMs) };
        _timer.Tick += (_, _) => BeginClose();
        MouseEnter += (_, _) => PauseCountdown();
        MouseLeave += (_, _) => ResumeCountdown();
    }

    /// <summary>
    /// Names the control a notification came from when the caller did not say. The
    /// icon already identifies the source for most hotkey actions, so this covers them
    /// without every call site having to pass a description.
    /// </summary>
    private static string DescribeSource(ToastIcon icon) => icon switch
    {
        ToastIcon.BrightnessUp or ToastIcon.BrightnessDown => "Display brightness",
        ToastIcon.BacklightUp or ToastIcon.BacklightDown => "Keyboard backlight",
        ToastIcon.Touchpad => "Touchpad",
        ToastIcon.Microphone or ToastIcon.MicrophoneMute => "Microphone",
        ToastIcon.Battery => "On battery",
        ToastIcon.Charger => "Plugged in",
        ToastIcon.FnLock => "Keyboard",
        ToastIcon.Controller => "Controller mode",
        _ => "Arsenal"
    };

    /// <summary>
    /// Measures the card before its HWND is created. ToastManager uses this size to
    /// assign Left and Top before Show(), preventing a new topmost layered window from
    /// being inserted at WPF's default coordinates for even a single DWM frame.
    /// </summary>
    public void PrepareForPlacement()
    {
        if (Content is not UIElement content) return;

        content.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        _preparedHeight = content.DesiredSize.Height;
    }

    /// <summary>
    /// Reflows the card to a new slot in the stack by animating the window itself.
    /// </summary>
    /// <remarks>
    /// This used to move the HWND to its destination in one step and counter-translate
    /// the rendered card to keep it visually still. Those are two different pipelines:
    /// the move is a SetWindowPos the compositor can present at once, while the
    /// counter-translation only reaches it on WPF's next render pass. For the frame in
    /// between, the card stood at its destination - the flash of a toast arriving where
    /// it was going, vanishing, then sliding in from where it had been.
    ///
    /// Animating the position leaves the content untouched for the whole reflow, so
    /// there is no second pipeline to fall out of step with, and nothing re-uploads the
    /// layered surface frame by frame either - the compositor just moves the one it has.
    /// </remarks>
    public void SetPosition(double left, double top)
    {
        // An unrevealed toast has nothing on screen to keep continuous, so it snaps.
        // Its measured size only becomes exact once the HWND exists, and animating that
        // correction would drag the card sideways into its own entrance.
        if (!_hasPosition || !_opened || !SystemParameters.ClientAreaAnimation)
        {
            StopPositionAnimation();
            Left = left;
            Top = top;
            _hasPosition = true;
            return;
        }

        // Left and Top read back the animated value mid-flight, so a reflow interrupted
        // by another one continues from the frame currently on screen.
        if (Math.Abs(Left - left) < 0.5 && Math.Abs(Top - top) < 0.5) return;

        _leftEase.Start(Left, left, 240, Arsenal.UI.Controls.FrameEase.CubicOut, v => Left = v);
        _topEase.Start(Top, top, 240, Arsenal.UI.Controls.FrameEase.CubicOut, v => Top = v);
    }

    private void StopPositionAnimation()
    {
        _leftEase.Stop();
        _topEase.Stop();
    }

    /// <summary>
    /// Reveals a toast only after its native window has reached its final position.
    /// The one-time Window opacity change is deliberately not animated; all motion
    /// happens on the cached inner visual for smoother layered-window composition.
    /// </summary>
    public void Reveal()
    {
        if (_opened || _closing) return;
        _opened = true;
        Opacity = 1;
        BeginOpen();
    }

    public void UpdateLive(string message, string detail, int percentage, int settleMs = 1000)
    {
        if (_closing) return;

        _liveMode = true;
        _liveSettleMs = Math.Max(100, settleMs);
        int value = Math.Clamp(percentage, 0, 100);
        MessageText.Text = message;
        DetailText.Text = detail;
        LiveValueText.Text = $"{value}%";
        LiveValuePanel.Visibility = Visibility.Visible;
        ProgressTrack.Visibility = Visibility.Collapsed;

        double target = value / 100d;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _liveValueEase.Stop();
            LiveValueScale.ScaleX = target;
        }
        else
        {
            double current = LiveValueScale.ScaleX;
            _liveValueEase.Start(current, target, 120, Arsenal.UI.Controls.FrameEase.CubicOut, v => LiveValueScale.ScaleX = v);
        }

        if (IsLoaded) RestartLiveCountdown();
    }

    public void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        ClosingStarted?.Invoke(this);

        int fadeMs = SystemParameters.ClientAreaAnimation ? 180 : 1;
        int motionMs = SystemParameters.ClientAreaAnimation ? 210 : 1;
        var ease = Arsenal.UI.Controls.FrameEase.CubicIn;
        _motionFade.Start(MotionRoot.Opacity, 0, fadeMs, ease, v => MotionRoot.Opacity = v);
        _motionScaleX.Start(MotionScale.ScaleX, 0.985, motionMs, ease, v => MotionScale.ScaleX = v);
        _motionScaleY.Start(MotionScale.ScaleY, 0.985, motionMs, ease, v => MotionScale.ScaleY = v);

        // Leaves the way it arrived. Sign(0) is 0, so the unused axis animates to 0 and
        // stays put rather than drifting.
        _motionX.Start(MotionTransform.X, Math.Sign(_enterX) * ExitDistance, motionMs, ease, v => MotionTransform.X = v);
        _motionY.Start(MotionTransform.Y, Math.Sign(_enterY) * ExitDistance, motionMs, ease, v => MotionTransform.Y = v);
        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(motionMs + 12) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            _onClosed(this);
            Close();
        };
        closeTimer.Start();
    }

    private void BeginOpen()
    {
        MotionRoot.Opacity = 0;
        MotionTransform.X = _enterX;
        MotionTransform.Y = _enterY;
        MotionScale.ScaleX = 0.97;
        MotionScale.ScaleY = 0.97;

        int fadeMs = SystemParameters.ClientAreaAnimation ? 220 : 1;
        int motionMs = SystemParameters.ClientAreaAnimation ? 280 : 1;
        var ease = Arsenal.UI.Controls.FrameEase.CubicOut;
        _motionFade.Start(0, 1, fadeMs, ease, v => MotionRoot.Opacity = v);
        _motionScaleX.Start(0.97, 1, motionMs, ease, v => MotionScale.ScaleX = v);
        _motionScaleY.Start(0.97, 1, motionMs, ease, v => MotionScale.ScaleY = v);
        _motionX.Start(_enterX, 0, motionMs, ease, v => MotionTransform.X = v);
        _motionY.Start(_enterY, 0, motionMs, ease, v => MotionTransform.Y = v);
        if (_liveMode) RestartLiveCountdown(); else StartCountdown();
    }

    /// <summary>
    /// Starts the dismiss countdown from the beginning. Only used when the toast opens;
    /// hovering pauses and resumes so a toast you looked at is not shown for longer than
    /// one you ignored.
    /// </summary>
    private void StartCountdown()
    {
        _remainingMs = _durationMs;
        RunCountdown(1);
    }

    private void PauseCountdown()
    {
        if (_liveMode || _closing || !_timer.IsEnabled) return;

        _timer.Stop();
        int elapsed = (int)(DateTime.UtcNow - _countdownResumedAt).TotalMilliseconds;
        _remainingMs = Math.Max(0, _remainingMs - elapsed);

        if (ProgressTrack.Visibility != Visibility.Visible) return;

        // The ease writes the property directly, so stopping it simply leaves the bar
        // wherever it had reached.
        _progressEase.Stop();
    }

    private void ResumeCountdown()
    {
        if (_liveMode || _closing) return;
        if (_remainingMs <= 0) { BeginClose(); return; }
        RunCountdown(_durationMs <= 0 ? 0 : (double)_remainingMs / _durationMs);
    }

    /// <param name="fromProgress">Where the progress bar restarts, 1 being full.</param>
    private void RunCountdown(double fromProgress)
    {
        _countdownResumedAt = DateTime.UtcNow;
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _remainingMs));
        _timer.Start();

        if (ProgressTrack.Visibility != Visibility.Visible) return;
        ProgressScale.ScaleX = fromProgress;
        _progressEase.Start(fromProgress, 0, Math.Max(1, _remainingMs), Arsenal.UI.Controls.FrameEase.Linear, v => ProgressScale.ScaleX = v);
    }

    private readonly Arsenal.UI.Controls.FrameEase _leftEase = new();
    private readonly Arsenal.UI.Controls.FrameEase _topEase = new();
    private readonly Arsenal.UI.Controls.FrameEase _liveValueEase = new();
    private readonly Arsenal.UI.Controls.FrameEase _motionFade = new();
    private readonly Arsenal.UI.Controls.FrameEase _motionScaleX = new();
    private readonly Arsenal.UI.Controls.FrameEase _motionScaleY = new();
    private readonly Arsenal.UI.Controls.FrameEase _motionX = new();
    private readonly Arsenal.UI.Controls.FrameEase _motionY = new();
    private readonly Arsenal.UI.Controls.FrameEase _progressEase = new();

    private void RestartLiveCountdown()
    {
        if (_closing) return;
        _timer.Stop();
        _remainingMs = _liveSettleMs;
        _countdownResumedAt = DateTime.UtcNow;
        _timer.Interval = TimeSpan.FromMilliseconds(_liveSettleMs);
        _timer.Start();
    }

    private void ApplyDesign(int style)
    {
        switch (style)
        {
            case 1: // Compact
                // Window width, so it has to carry the slack on both sides too -
                // setting the bare card width here would leave the card 48px narrower
                // than intended and misplace it against the screen edge.
                Width = 310 + (Slack * 2);
                ToastChrome.Padding = new Thickness(11);
                ToastChrome.CornerRadius = new CornerRadius(11);
                AccentBar.Visibility = Visibility.Collapsed;
                break;
            case 2: // Accent
                ToastChrome.Background = new SolidColorBrush(MediaColor.FromArgb(248, 25, 42, 55));
                ToastChrome.BorderBrush = (MediaBrush)FindResource("AccentPrimary");
                break;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => BeginClose();
}
