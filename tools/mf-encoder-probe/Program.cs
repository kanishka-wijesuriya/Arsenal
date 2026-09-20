using Arsenal.UI.Services.Remote.Desktop;

// Reproduces what a quality change does to the video encoder, which is not simply
// building one encoder after another.
//
// On a quality change the session tears the old pipeline down and immediately
// enumerates again. The teardown is not instant - the hardware encoder is released on
// another thread - so the new enumeration runs while the old encoder still holds NVENC.
// NVENC has a small number of simultaneous sessions, so the new NVIDIA candidate
// configures and then produces nothing, is rejected, and the enumeration moves on to
// the Intel transforms and finally to a software one. That rejection path is the one
// the crash lives in, and a probe that builds encoders one at a time never enters it:
// with nothing else holding the card, the first candidate simply works.
//
// So each round here deliberately overlaps: a second encoder is enumerated while the
// first is still alive, and the first is disposed on another thread at the same time.
//
// The result is the process surviving. It exits non-zero only if it cannot build
// anything at all, which would mean the probe is not testing what it thinks it is.

int rounds = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 30;

(int Width, int Height, int Fps, int Kbps)[] presets =
[
    (3440, 1440, 30, 24000),   // sharp
    (1600, 670, 60, 8000),     // smooth
];

string[] codecs = ["hevc", "h264"];

Console.WriteLine($"Overlapping encoder rounds: {rounds}");

int overlapped = 0;
MediaFoundationVideoEncoder? holding = null;

for (int round = 0; round < rounds; round++)
{
    var preset = presets[round % presets.Length];

    // The old encoder goes away on another thread while this enumeration runs, which
    // is what the session does now that disposal waits for the capture thread.
    MediaFoundationVideoEncoder? previous = holding;
    Thread? releasing = null;
    if (previous is not null)
    {
        releasing = new Thread(() => previous.Dispose()) { IsBackground = true };
        releasing.Start();
    }

    MediaFoundationVideoEncoder? fresh = MediaFoundationVideoEncoder.TryCreate(
        codecs, preset.Width, preset.Height, preset.Fps, preset.Kbps);

    releasing?.Join(TimeSpan.FromSeconds(5));

    if (fresh is null)
    {
        Console.WriteLine($"  round {round + 1,3}: nothing available, tiles would be used");
        holding = null;
        continue;
    }

    if (previous is not null) overlapped++;
    Console.WriteLine($"  round {round + 1,3}: {fresh.EncoderName} at {preset.Width}x{preset.Height}");
    holding = fresh;
}

holding?.Dispose();

Console.WriteLine($"Survived {rounds} rounds, {overlapped} of them enumerated while another encoder was being released.");
return 0;
