namespace Arsenal.Helpers
{
    // envelope and hue state for audio driven lighting, one instance per device
    public class AudioLevels
    {
        public const int Bands = 8;

        public double[] Bars { get; } = new double[Bands];
        public double Scale { get; private set; } = 1;
        public double Brightness { get; private set; }

        private readonly List<double> maxes = new();
        private readonly double decay = AppConfig.Get("audio_decay", 70) / 100.0;
        public double DecayMultiplier { get; init; } = 1;
        private long lastPresent;
        private double peak;
        private double envelope;
        private double smoothedHue;

        public void Reset()
        {
            maxes.Clear();
            lastPresent = 0;
            envelope = 0;
            smoothedHue = 0;
        }

        public bool Update(double[] fftMag)
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (Math.Abs(now - lastPresent) < 50) return false;
            lastPresent = now;

            if (fftMag.Length < Bands) return false;

            peak = 0;
            for (int i = 0; i < Bands; i++)
            {
                Bars[i] = Math.Sqrt(fftMag[i] * 10000);
                if (Bars[i] > peak) peak = Bars[i];
            }

            maxes.Add(peak);
            if (maxes.Count > 100) maxes.RemoveAt(0);
            Scale = Math.Max(1, maxes.OrderByDescending(x => x).ElementAt(maxes.Count / 10));

            envelope = Math.Max(envelope * decay * DecayMultiplier, peak);
            double brightness = Math.Min(1.0, envelope / Scale);
            Brightness = brightness * brightness * brightness;
            return true;
        }

        public Color DominantColor(Color baseColor)
        {
            int dominant = 1;
            double dominantWeighted = Bars[1];
            for (int i = 2; i < Bands; i++)
            {
                double w = Bars[i] * (1 + (i - 1) * 0.15);
                if (w > dominantWeighted) { dominantWeighted = w; dominant = i; }
            }

            if (peak > Scale * 0.3)
            {
                double baseHue = ColorUtils.HSV.ToHSV(baseColor).Hue;
                double targetHue = (baseHue + (dominant - 1) / (double)(Bands - 2) * (2.0 / 3.0)) % 1.0;
                smoothedHue = smoothedHue * 0.6 + targetHue * 0.4;
            }

            return new ColorUtils.HSV { Hue = smoothedHue, Saturation = 1.0, Value = Brightness }.ToRGB();
        }
    }
}
