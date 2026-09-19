using System.Windows;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows;

/// <summary>What a person at the desktop decided about an incoming session.</summary>
public sealed record RemoteConsentAnswer(bool Allowed, bool ViewOnly, bool Remember);

/// <summary>
/// Asks the person at the machine before a phone takes it over.
/// </summary>
/// <remarks>
/// It refuses on its own when the countdown runs out rather than accepting, and there is
/// no way to answer it by ignoring it. A prompt that grants control to whoever waits
/// long enough is decoration.
/// </remarks>
public partial class RemoteConsentWindow : Window
{
    private readonly DispatcherTimer _countdown;
    private readonly TaskCompletionSource<RemoteConsentAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _secondsLeft;

    public RemoteConsentWindow(string deviceName, string address, int seconds)
    {
        InitializeComponent();
        RequesterText.Text = string.IsNullOrWhiteSpace(address) ? deviceName : deviceName + "  ·  " + address;
        _secondsLeft = Math.Max(5, seconds);
        UpdateCountdown();

        _countdown = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            _secondsLeft--;
            UpdateCountdown();
            if (_secondsLeft <= 0) Finish(new RemoteConsentAnswer(false, false, false));
        };
        _countdown.Start();

        // Closing the window by any other route is a refusal, not a dismissal.
        Closed += (_, _) => Finish(new RemoteConsentAnswer(false, false, false));
        Loaded += (_, _) => { Activate(); RefuseButton.Focus(); };
    }

    public Task<RemoteConsentAnswer> Answer => _answer.Task;

    private void UpdateCountdown() =>
        CountdownText.Text = "Refusing automatically in " + Math.Max(0, _secondsLeft) + "s";

    private void Allow_Click(object sender, RoutedEventArgs e) =>
        Finish(new RemoteConsentAnswer(true, ViewOnlyCheck.IsChecked == true, RememberCheck.IsChecked == true));

    private void Refuse_Click(object sender, RoutedEventArgs e) =>
        Finish(new RemoteConsentAnswer(false, false, RememberCheck.IsChecked == true));

    private void Finish(RemoteConsentAnswer answer)
    {
        _countdown.Stop();
        _answer.TrySetResult(answer);
        if (IsLoaded) Close();
    }
}
