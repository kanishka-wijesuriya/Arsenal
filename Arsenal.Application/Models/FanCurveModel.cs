using System.Collections.ObjectModel;

namespace Arsenal.Application.Models
{
    public class FanPoint
    {
        public int Temperature { get; set; }
        public int Percentage { get; set; }

        public FanPoint() { }
        public FanPoint(int temp, int percent)
        {
            Temperature = temp;
            Percentage = percent;
        }
    }

    public class FanCurveModel
    {
        public int FanIndex { get; set; }
        public string FanName { get; set; } = "CPU Fan";
        public ObservableCollection<FanPoint> Points { get; set; } = new();

        public static FanCurveModel CreateDefault(int fanIndex, string name)
        {
            var model = new FanCurveModel
            {
                FanIndex = fanIndex,
                FanName = name
            };

            // Standard 8 points curve
            model.Points.Add(new FanPoint(30, 0));
            model.Points.Add(new FanPoint(40, 15));
            model.Points.Add(new FanPoint(50, 25));
            model.Points.Add(new FanPoint(60, 40));
            model.Points.Add(new FanPoint(70, 55));
            model.Points.Add(new FanPoint(80, 70));
            model.Points.Add(new FanPoint(90, 85));
            model.Points.Add(new FanPoint(100, 100));

            return model;
        }

        public byte[] ToByteArray()
        {
            byte[] bytes = new byte[16];
            for (int i = 0; i < 8 && i < Points.Count; i++)
            {
                bytes[i] = (byte)Math.Clamp(Points[i].Temperature, 0, 120);
                bytes[i + 8] = (byte)Math.Clamp(Points[i].Percentage, 0, 100);
            }
            return bytes;
        }

        public static FanCurveModel FromByteArray(int fanIndex, string name, byte[] data)
        {
            var model = new FanCurveModel
            {
                FanIndex = fanIndex,
                FanName = name
            };

            if (data == null || data.Length < 16)
                return CreateDefault(fanIndex, name);

            for (int i = 0; i < 8; i++)
            {
                model.Points.Add(new FanPoint(data[i], data[i + 8]));
            }

            return model;
        }
    }
}
