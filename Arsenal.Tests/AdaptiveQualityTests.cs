using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The rules that decide how much of the link a session is allowed to use.
/// </summary>
/// <remarks>
/// Worth testing without a network because every failure here is quiet. A controller
/// that backs off and never recovers gives a session that degrades once and stays poor;
/// one that probes too eagerly oscillates; one whose floor is wrong keeps sending after
/// the picture has stopped being worth looking at. None of those throws, and none of them
/// is obvious from watching a session that happens to be on a good link.
/// </remarks>
public class AdaptiveQualityTests
{
    private const int Ceiling = 10000;
    private const int Fps = 30;

    private static LinkSample Clean(int kbps = 4000) =>
        new(BytesFor(kbps), StallUs: 0, IntervalUs: 2_000_000, Dropped: 0, QueueDepth: 0);

    private static LinkSample Stalled(int drainKbps, double fraction = 0.5) =>
        new(BytesFor(drainKbps), (long)(2_000_000 * fraction), 2_000_000, Dropped: 0, QueueDepth: 4);

    /// <summary>Bytes that represent the given rate over the two second interval.</summary>
    private static long BytesFor(int kbps) => (long)kbps * 1000 / 8 * 2;

    private static AdaptiveQuality Fresh(int starting = Ceiling) => new(Ceiling, Fps, starting);

    [Fact]
    public void Starts_at_the_preset_when_nothing_suggests_otherwise()
    {
        var quality = Fresh();
        Assert.Equal(Ceiling, quality.Current.BitrateKbps);
        Assert.Equal(Fps, quality.Current.FrameRate);
        Assert.False(quality.Limited);
    }

    [Fact]
    public void A_clean_interval_changes_nothing_at_the_ceiling()
    {
        var quality = Fresh();
        quality.Observe(Clean());
        quality.Observe(Clean());
        Assert.Equal(Ceiling, quality.Current.BitrateKbps);
    }

    /// <remarks>
    /// The point of measuring the drain rate rather than halving: a link that carried
    /// 3000 kbps while saturated should land just under 3000, not at 5000 and then 2500.
    /// </remarks>
    [Fact]
    public void Backs_off_to_just_under_what_the_link_actually_carried()
    {
        var quality = Fresh();
        quality.Observe(Stalled(drainKbps: 3000));

        Assert.InRange(quality.Current.BitrateKbps, 2400, 2800);
        Assert.True(quality.Limited);
    }

    [Fact]
    public void One_stalled_interval_is_enough_to_act_on()
    {
        var quality = Fresh();
        int before = quality.Current.BitrateKbps;
        quality.Observe(Stalled(drainKbps: 2000));
        Assert.True(quality.Current.BitrateKbps < before);
    }

    [Fact]
    public void A_brief_block_is_not_treated_as_congestion()
    {
        var quality = Fresh();
        quality.Observe(Stalled(drainKbps: 9000, fraction: 0.05));
        Assert.Equal(Ceiling, quality.Current.BitrateKbps);
    }

    [Fact]
    public void Dropped_frames_alone_nudge_down_rather_than_collapse()
    {
        var quality = Fresh();
        quality.Observe(new LinkSample(BytesFor(9000), StallUs: 0, IntervalUs: 2_000_000, Dropped: 3, QueueDepth: 8));

        Assert.True(quality.Current.BitrateKbps < Ceiling);
        Assert.True(quality.Current.BitrateKbps > Ceiling / 2);
    }

    [Fact]
    public void Recovers_upward_after_the_link_stays_clean()
    {
        var quality = Fresh();
        quality.Observe(Stalled(drainKbps: 2000));
        int low = quality.Current.BitrateKbps;

        for (int i = 0; i < 12; i++) quality.Observe(Clean(low));

        Assert.True(quality.Current.BitrateKbps > low);
    }

    /// <remarks>
    /// Rising must not be instant. A single quiet interval right after a stall is the
    /// backoff working, not the link improving.
    /// </remarks>
    [Fact]
    public void Does_not_rise_on_the_very_first_clean_interval()
    {
        var quality = Fresh();
        quality.Observe(Stalled(drainKbps: 2000));
        int low = quality.Current.BitrateKbps;

        quality.Observe(Clean(low));

        Assert.Equal(low, quality.Current.BitrateKbps);
    }

    [Fact]
    public void Never_rises_above_the_preset()
    {
        var quality = Fresh();
        for (int i = 0; i < 50; i++) quality.Observe(Clean());
        Assert.Equal(Ceiling, quality.Current.BitrateKbps);
    }

    /// <remarks>
    /// A link that will carry almost nothing must stop at a floor rather than converge
    /// on zero: below this the session should be saying the network is the problem.
    /// </remarks>
    [Fact]
    public void Stops_at_a_floor_rather_than_shrinking_to_nothing()
    {
        var quality = Fresh();
        for (int i = 0; i < 40; i++) quality.Observe(Stalled(drainKbps: 10));

        Assert.True(quality.Current.BitrateKbps >= 500);
    }

    [Fact]
    public void Gives_up_frame_rate_only_after_bitrate_has_run_out()
    {
        var quality = Fresh();

        // Down to the floor first; frame rate must still be untouched on the way.
        for (int i = 0; i < 6; i++)
        {
            quality.Observe(Stalled(drainKbps: 300));
            if (quality.Current.FrameRate < Fps)
            {
                Assert.True(quality.Current.BitrateKbps <= 900,
                    "frame rate was reduced while bitrate still had room");
            }
        }

        for (int i = 0; i < 10; i++) quality.Observe(Stalled(drainKbps: 50));
        Assert.True(quality.Current.FrameRate < Fps);
        Assert.True(quality.Current.FrameRate >= 15);
    }

    [Fact]
    public void Frame_rate_comes_back_before_bitrate_does()
    {
        var quality = Fresh();
        for (int i = 0; i < 20; i++) quality.Observe(Stalled(drainKbps: 50));

        Assert.True(quality.Current.FrameRate < Fps);
        int reducedBitrate = quality.Current.BitrateKbps;

        quality.Observe(Clean(200));
        quality.Observe(Clean(200));

        Assert.True(quality.Current.FrameRate > 15);
        Assert.Equal(reducedBitrate, quality.Current.BitrateKbps);
    }

    [Fact]
    public void An_interval_with_no_elapsed_time_is_ignored()
    {
        var quality = Fresh();
        QualityTarget before = quality.Current;
        quality.Observe(new LinkSample(0, 0, IntervalUs: 0, Dropped: 5, QueueDepth: 8));
        Assert.False(quality.Current.Differs(before));
    }

    [Fact]
    public void A_starting_estimate_is_honoured_but_never_exceeds_the_preset()
    {
        Assert.Equal(2000, new AdaptiveQuality(Ceiling, Fps, 2000).Current.BitrateKbps);
        Assert.Equal(Ceiling, new AdaptiveQuality(Ceiling, Fps, 999999).Current.BitrateKbps);
    }

    [Fact]
    public void The_link_estimate_stays_within_the_preset()
    {
        int estimate = AdaptiveQuality.EstimateStartingKbps(Ceiling);
        Assert.InRange(estimate, 1, Ceiling);
    }
}
