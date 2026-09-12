using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The rules that decide whether a phone on the network can pair with this machine.
/// </summary>
/// <remarks>
/// Before these rules existed, the bridge served /v1/pair from the moment it started,
/// minted a code on demand, including in response to an incoming request, and
/// answered a wrong code with a bare 403: no counter, no delay, no lockout. Six digits
/// against unlimited guesses is not a secret on a shared network.
///
/// Each test below names a property the fix depends on. If one of them ever goes red,
/// the six-digit code has stopped being safe, however well the rest of the bridge works.
/// </remarks>
public class PairingWindowTests
{
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private PairingWindow NewWindow(string code = "123456") =>
        new(now: () => _now, newCode: () => code);

    private void Advance(TimeSpan by) => _now += by;

    [Fact]
    public void StartsClosed()
    {
        // The bridge runs from every logon. A code minted at startup would be a live
        // target for as long as the machine is on, for a screen nobody is looking at.
        var window = NewWindow();

        Assert.False(window.IsOpen);
        Assert.Equal(PairingAttempt.Rejected, window.Submit("123456"));
    }

    [Fact]
    public void SubmittingDoesNotBringACodeIntoExistence()
    {
        // The property that matters most. /v1/pair is reachable by anyone who can route
        // to the machine; if guessing could mint the answer, guessing always eventually
        // wins.
        var window = NewWindow();

        for (int i = 0; i < 50; i++) Assert.Equal(PairingAttempt.Rejected, window.Submit("123456"));

        Assert.False(window.IsOpen);
    }

    [Fact]
    public void OpensWhenThePairingScreenAsksForTheCode()
    {
        var window = NewWindow();

        string code = window.Peek();

        Assert.True(window.IsOpen);
        Assert.Equal("123456", code);
        Assert.Equal(PairingAttempt.Accepted, window.Submit(code));
    }

    [Fact]
    public void ACorrectCodeIsConsumed()
    {
        // Two phones racing must not both pair on one code, and the window ends with the
        // pairing rather than staying open for whoever asks next.
        var window = NewWindow();
        string code = window.Peek();

        Assert.Equal(PairingAttempt.Accepted, window.Submit(code));
        Assert.Equal(PairingAttempt.Rejected, window.Submit(code));
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void FiveWrongCodesCloseTheWindow()
    {
        var window = NewWindow();
        window.Peek();

        for (int i = 1; i < PairingWindow.MaxFailures; i++)
            Assert.Equal(PairingAttempt.Rejected, window.Submit("000000"));

        Assert.Equal(PairingAttempt.LockedOut, window.Submit("000000"));
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void TheRightCodeAfterALockoutIsStillRefused()
    {
        // The lockout has to survive the attacker finally guessing right, or it only
        // delays them rather than stopping them.
        var window = NewWindow();
        string code = window.Peek();

        for (int i = 0; i < PairingWindow.MaxFailures; i++) window.Submit("000000");

        Assert.Equal(PairingAttempt.Rejected, window.Submit(code));
    }

    [Fact]
    public void ReopeningAfterALockoutIssuesADifferentCode()
    {
        // Everything the attacker learned is worthless once the person reopens the
        // screen, which is what caps them at five guesses per deliberate human act.
        int issued = 0;
        var window = new PairingWindow(now: () => _now, newCode: () => $"{++issued:D6}");

        string first = window.Peek();
        for (int i = 0; i < PairingWindow.MaxFailures; i++) window.Submit("999999");

        string second = window.Reissue();

        Assert.NotEqual(first, second);
        Assert.Equal(PairingAttempt.Rejected, window.Submit(first));
        Assert.Equal(PairingAttempt.Accepted, window.Submit(second));
    }

    [Fact]
    public void AFailureCountResetsWhenANewCodeIsIssued()
    {
        var window = NewWindow();
        window.Peek();
        window.Submit("000000");
        window.Submit("000000");

        window.Reissue();

        // Four more wrong answers must not trip the lockout on the fresh code.
        for (int i = 0; i < PairingWindow.MaxFailures - 1; i++)
            Assert.Equal(PairingAttempt.Rejected, window.Submit("000000"));
        Assert.True(window.IsOpen);
    }

    [Fact]
    public void ClosingDiscardsTheCode()
    {
        // What leaving the pairing screen does.
        var window = NewWindow();
        string code = window.Peek();

        window.Close();

        Assert.False(window.IsOpen);
        Assert.Equal(PairingAttempt.Rejected, window.Submit(code));
    }

    [Fact]
    public void TheWindowLapsesOnceNobodyIsLooking()
    {
        var window = NewWindow();
        string code = window.Peek();

        Advance(PairingWindow.DefaultDuration + TimeSpan.FromSeconds(1));

        Assert.False(window.IsOpen);
        Assert.Equal(PairingAttempt.Rejected, window.Submit(code));
    }

    [Fact]
    public void ReadingTheCodeSlidesTheWindowForward()
    {
        // The pairing screen re-reads on a tick while it is visible, which is how the
        // window stays open exactly as long as somebody is actually on it.
        var window = NewWindow();
        window.Peek();

        for (int i = 0; i < 10; i++)
        {
            Advance(TimeSpan.FromMinutes(4));
            window.Peek();
            Assert.True(window.IsOpen);
        }
    }

    [Fact]
    public void AnExpiredCodeIsReplacedRatherThanReused()
    {
        int issued = 0;
        var window = new PairingWindow(now: () => _now, newCode: () => $"{++issued:D6}");
        string first = window.Peek();

        Advance(PairingWindow.CodeLifetime + TimeSpan.FromSeconds(1));
        string second = window.Peek();

        Assert.NotEqual(first, second);
        Assert.Equal(PairingAttempt.Rejected, window.Submit(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("12345678901234567890")]
    [InlineData("abcdef")]
    public void MalformedInputIsRejectedWithoutThrowing(string? presented)
    {
        // Whatever arrives on the wire reaches this. It must be a rejection, never an
        // exception on the connection thread.
        var window = NewWindow();
        window.Peek();

        Assert.Equal(PairingAttempt.Rejected, window.Submit(presented));
    }

    [Fact]
    public void ShortCodesArePaddedTheSameWayTheyAreIssued()
    {
        // A code with leading zeros is issued as "000042" and may be typed as "42".
        var window = new PairingWindow(now: () => _now, newCode: () => "000042");
        window.Peek();

        Assert.Equal(PairingAttempt.Accepted, window.Submit("42"));
    }

    [Fact]
    public void ConcurrentAttemptsCannotBothPair()
    {
        // Two phones, one code, at the same moment.
        var window = NewWindow();
        string code = window.Peek();

        int accepted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (window.Submit(code) == PairingAttempt.Accepted) Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
    }
}
