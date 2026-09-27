using System;

namespace Arsenal.Application.Models;

/// <summary>What the link did over one interval, as the sender saw it.</summary>
public readonly record struct LinkSample(
    long BytesWritten,
    long StallUs,
    long IntervalUs,
    int Dropped,
    int QueueDepth);

/// <summary>What the session should be sending, after looking at the link.</summary>
public readonly record struct QualityTarget(int BitrateKbps, int FrameRate)
{
    public bool Differs(QualityTarget other) =>
        BitrateKbps != other.BitrateKbps || FrameRate != other.FrameRate;
}

/// <summary>
/// Matches what is sent to what the link will carry.
/// </summary>
/// <remarks>
/// The measurement is sender side and deliberately so. This session runs over TCP, which
/// hides loss and reorders nothing, so the receiver has very little to report that the
/// sender cannot already see: the two signals that matter are how long the socket blocks
/// and how fast it drains, and both are here.
///
/// <para><b>Stall time is the primary signal.</b> A TCP write returns as soon as the
/// socket buffer will take the bytes, so while there is headroom it costs nothing. The
/// moment the link is the bottleneck the buffer stays full and the write blocks, and the
/// fraction of the interval spent blocked is a direct, continuous measure of how far over
/// capacity the stream is. Nothing has to be inferred from loss, and there is no
/// round-trip to wait for.</para>
///
/// <para><b>Drain rate is what it is worth backing off to.</b> While the writer is
/// stalling it is by definition sending as fast as the link allows, so the bytes that got
/// through during a stalled interval are the link's actual capacity for this traffic. The
/// target drops to a little under that rather than to an arbitrary fraction of where it
/// was, which is the difference between converging in one step and halving repeatedly
/// past the right answer.</para>
///
/// <para><b>Going back up is slow and it probes.</b> Capacity cannot be measured without
/// using it, so after a quiet spell the target is raised by a fraction and the next
/// interval says whether that was allowed. Rising in small steps and falling in one large
/// one is the standard asymmetry, and it is what keeps a brief interruption from costing
/// a minute of poor picture.</para>
///
/// <para>Bitrate moves first and frame rate only when bitrate has run out of room. A
/// lower bitrate at the same frame rate looks soft; a lower frame rate looks broken, and
/// on a desktop being typed at the softness is much the better trade. Resolution is not
/// touched at all: changing it means rebuilding the encoder and restarting the decoder on
/// the phone, which is a visible interruption, and the phone is scaling the picture to
/// its own screen anyway.</para>
/// </remarks>
public sealed class AdaptiveQuality
{
    /// <summary>Above this fraction of an interval spent blocked, the link is the limit.</summary>
    /// <remarks>
    /// Not zero. A write blocking briefly is normal on any link - it is how flow control
    /// works - and reacting to every millisecond would make the target oscillate on a
    /// connection that is perfectly healthy.
    /// </remarks>
    private const double StallFraction = 0.15;

    /// <summary>How much of the measured drain rate to aim for after a stall.</summary>
    /// <remarks>
    /// Below one so the link gets room to clear what is already queued. Aiming at exactly
    /// the measured rate leaves the buffer as full as it was and the next interval stalls
    /// again at the new target.
    /// </remarks>
    private const double BackoffShare = 0.85;

    /// <summary>How much to add when the link has been clean.</summary>
    private const double ProbeStep = 0.12;

    /// <summary>Intervals of quiet before probing upward.</summary>
    private const int QuietBeforeProbe = 2;

    private readonly int _ceilingKbps;
    private readonly int _floorKbps;
    private readonly int _ceilingFps;
    private readonly int _floorFps;

    private int _bitrateKbps;
    private int _frameRate;
    private int _quiet;

    public AdaptiveQuality(int presetBitrateKbps, int presetFrameRate, int startingKbps)
    {
        _ceilingKbps = Math.Max(500, presetBitrateKbps);
        _ceilingFps = Math.Max(1, presetFrameRate);

        // The floor is a picture that is still worth looking at. Below roughly this the
        // session should be saying the network is the problem, not quietly degrading
        // until the screen is unreadable.
        _floorKbps = Math.Min(_ceilingKbps, 800);
        _floorFps = Math.Min(_ceilingFps, 15);

        _bitrateKbps = Math.Clamp(startingKbps <= 0 ? _ceilingKbps : startingKbps, _floorKbps, _ceilingKbps);
        _frameRate = _ceilingFps;
    }

    public QualityTarget Current => new(_bitrateKbps, _frameRate);

    /// <summary>
    /// Why the target last moved, for the session to log.
    /// </summary>
    /// <remarks>
    /// Reported rather than written here so this class stays a decision and nothing else:
    /// it has no socket, no encoder and no log, which is what lets the whole of it be
    /// tested without any of them.
    /// </remarks>
    public string? LastChange { get; private set; }

    /// <summary>Whether the link is currently being held below what the preset asked for.</summary>
    public bool Limited => _bitrateKbps < _ceilingKbps || _frameRate < _ceilingFps;

    /// <summary>
    /// Folds one interval's measurements in and returns what to send now.
    /// </summary>
    public QualityTarget Observe(LinkSample sample)
    {
        if (sample.IntervalUs <= 0) return Current;

        double stalled = Math.Clamp(sample.StallUs / (double)sample.IntervalUs, 0d, 1d);
        double seconds = sample.IntervalUs / 1_000_000d;
        int drainKbps = (int)Math.Round(sample.BytesWritten * 8d / 1000d / seconds);

        bool struggling = stalled >= StallFraction || sample.Dropped > 0;

        if (struggling)
        {
            _quiet = 0;

            // The link carried drainKbps while it was saturated, so that is its capacity
            // for this traffic. Backing off to a share of a measured number converges
            // where repeated halving overshoots.
            int measured = drainKbps > 0 ? (int)Math.Round(drainKbps * BackoffShare) : _bitrateKbps / 2;

            // A drop with no stall means the encoder outran the writer rather than the
            // link being full; that deserves a nudge down, not a collapse to the
            // measured rate.
            int proposed = stalled >= StallFraction ? measured : (int)Math.Round(_bitrateKbps * 0.85);
            int next = Math.Clamp(Math.Min(proposed, _bitrateKbps), _floorKbps, _ceilingKbps);

            if (next < _bitrateKbps)
            {
                LastChange = $"{_bitrateKbps} -> {next} kbps (link {drainKbps} kbps, blocked {stalled * 100:F0}% of the interval, {sample.Dropped} dropped)";
                _bitrateKbps = next;
                return Current;
            }

            // Bitrate is already on the floor and the link still cannot keep up. The
            // only lever left that does not restart the phone's decoder is sending
            // fewer pictures.
            if (_frameRate > _floorFps)
            {
                int slower = Math.Max(_floorFps, _frameRate - 10);
                LastChange = $"{_frameRate} -> {slower} fps; bitrate is already at its floor";
                _frameRate = slower;
            }

            return Current;
        }

        // Clean interval. Give the link a couple of them before asking for more, so a
        // single quiet second after a stall does not immediately undo the backoff.
        if (++_quiet < QuietBeforeProbe) return Current;
        _quiet = 0;

        if (_frameRate < _ceilingFps)
        {
            // Frame rate comes back first: it was taken last, and a stuttering picture
            // is worse than a soft one.
            _frameRate = Math.Min(_ceilingFps, _frameRate + 10);
            LastChange = $"back up to {_frameRate} fps";
            return Current;
        }

        if (_bitrateKbps < _ceilingKbps)
        {
            int next = Math.Min(_ceilingKbps, (int)Math.Round(_bitrateKbps * (1 + ProbeStep)) + 50);
            if (next > _bitrateKbps)
            {
                _bitrateKbps = next;
                LastChange = $"probing up to {_bitrateKbps} kbps";
            }
        }

        return Current;
    }

    /// <summary>
    /// A first guess at what the link will carry, from the adapter carrying the session.
    /// </summary>
    /// <remarks>
    /// Only a starting point, and a weak one. A wireless link rate says what the radio
    /// negotiated, not what share of the air this connection will get, and a gigabit
    /// adapter on the desktop end says nothing about the phone's half of the path. It is
    /// here because starting a session at a sane rate and converging is better than
    /// opening every session at the preset ceiling and stalling for the first few
    /// seconds - but the measurement above is what decides, within a second or two.
    /// </remarks>
    public static int EstimateStartingKbps(int presetBitrateKbps)
    {
        try
        {
            long best = 0;
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                best = Math.Max(best, adapter.Speed);
            }

            if (best <= 0) return presetBitrateKbps;

            // A small share of the link, because the number is the local adapter's and
            // the path is only as good as the phone's end of it.
            long share = best / 1000 / 20;
            return (int)Math.Clamp(share, 1500, presetBitrateKbps);
        }
        catch
        {
            return presetBitrateKbps;
        }
    }
}
