using Arsenal.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// The desktop's own output, on its way to the phone.
/// </summary>
/// <remarks>
/// WASAPI loopback reads what the default render endpoint is playing, which is the whole
/// mix rather than any one application, and it does it without a virtual cable or a
/// driver. It delivers nothing at all while the desktop is silent, so an idle session
/// costs no bandwidth and needs no silence detection of its own.
///
/// <para>The samples travel as 16-bit PCM at the endpoint's own rate rather than being
/// resampled or coded. On a LAN that is about 1.5 Mbit/s for stereo at 48 kHz, which is
/// a fraction of what the screen costs, and it removes an encoder, a decoder and their
/// combined latency from the path. The economy preset folds to mono, which halves it
/// again for anything that was not stereo content to begin with.</para>
///
/// <para>The endpoint's format is reported to the phone rather than forced: asking
/// WASAPI for a rate the hardware does not run at fails the whole capture, and Android's
/// AudioTrack resamples happily.</para>
/// </remarks>
internal sealed class RemoteAudioCapture : IDisposable
{
    private readonly Action<byte[], int, long> _onSamples;
    private readonly bool _mono;
    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _enumerator;
    private byte[] _converted = Array.Empty<byte>();
    private bool _disposed;

    internal RemoteAudioCapture(bool mono, Action<byte[], int, long> onSamples)
    {
        _mono = mono;
        _onSamples = onSamples;
    }

    internal RemoteAudioFormat? Format { get; private set; }

    internal bool Start()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            using MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _capture = new WasapiLoopbackCapture(device);

            WaveFormat source = _capture.WaveFormat;
            int channels = _mono ? 1 : Math.Min(2, source.Channels);
            Format = new RemoteAudioFormat(source.SampleRate, channels, 16);

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote audio: " + ex.Message);
            Format = null;
            Dispose();
            return false;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Logger.WriteLine("Remote audio stopped: " + e.Exception.Message);
    }

    /// <summary>
    /// Converts one WASAPI buffer to the format the phone was promised.
    /// </summary>
    /// <remarks>
    /// Loopback hands back 32-bit float in the endpoint's channel count. Anything above
    /// stereo is folded down rather than truncated, because taking the first two channels
    /// of a 5.1 mix drops the centre channel, which on most content is the dialogue.
    /// </remarks>
    private void OnData(object? sender, WaveInEventArgs args)
    {
        if (_disposed || _capture is null || args.BytesRecorded <= 0) return;

        try
        {
            WaveFormat source = _capture.WaveFormat;
            int sourceChannels = Math.Max(1, source.Channels);
            int outputChannels = Format?.Channels ?? sourceChannels;
            bool isFloat = source.Encoding == WaveFormatEncoding.IeeeFloat
                || (source.Encoding == WaveFormatEncoding.Extensible && source.BitsPerSample == 32);

            int sourceBytesPerSample = source.BitsPerSample / 8;
            if (sourceBytesPerSample <= 0) return;
            int frames = args.BytesRecorded / (sourceBytesPerSample * sourceChannels);
            if (frames <= 0) return;

            int required = frames * outputChannels * 2;
            if (_converted.Length < required) _converted = new byte[required];

            int write = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                int frameOffset = frame * sourceBytesPerSample * sourceChannels;

                if (outputChannels == 1)
                {
                    float sum = 0;
                    for (int channel = 0; channel < sourceChannels; channel++)
                    {
                        sum += ReadSample(args.Buffer, frameOffset + channel * sourceBytesPerSample, sourceBytesPerSample, isFloat);
                    }
                    write = WriteSample(_converted, write, sum / sourceChannels);
                    continue;
                }

                for (int channel = 0; channel < outputChannels; channel++)
                {
                    float sample;
                    if (sourceChannels <= 2)
                    {
                        sample = ReadSample(args.Buffer, frameOffset + Math.Min(channel, sourceChannels - 1) * sourceBytesPerSample, sourceBytesPerSample, isFloat);
                    }
                    else
                    {
                        // Front pair plus half the centre, which is where dialogue lives.
                        float side = ReadSample(args.Buffer, frameOffset + channel * sourceBytesPerSample, sourceBytesPerSample, isFloat);
                        float centre = ReadSample(args.Buffer, frameOffset + 2 * sourceBytesPerSample, sourceBytesPerSample, isFloat);
                        sample = side + centre * 0.707f;
                    }
                    write = WriteSample(_converted, write, sample);
                }
            }

            _onSamples(_converted, write, RemoteClock.NowMicroseconds());
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote audio convert: " + ex.Message);
        }
    }

    private static float ReadSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        if (offset + bytesPerSample > buffer.Length) return 0;
        return bytesPerSample switch
        {
            4 when isFloat => BitConverter.ToSingle(buffer, offset),
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => ((buffer[offset + 2] << 16 | buffer[offset + 1] << 8 | buffer[offset]) << 8 >> 8) / 8388608f,
            _ => 0,
        };
    }

    private static int WriteSample(byte[] destination, int offset, float sample)
    {
        int value = (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767f);
        destination[offset] = (byte)(value & 0xFF);
        destination[offset + 1] = (byte)((value >> 8) & 0xFF);
        return offset + 2;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnData;
                _capture.RecordingStopped -= OnStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
        }
        catch (Exception ex) { Logger.WriteLine("Remote audio stop: " + ex.Message); }
        finally
        {
            _capture = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }
}
