using Arsenal.Helpers;
using Arsenal.USB;
using System.Runtime.InteropServices;

namespace Arsenal.Input
{
    public static class AmbientLight
    {
        [ComImport, Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
        private class SensorManager { }

        // parameterless methods are unused vtable slot placeholders

        [ComImport, Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISensorManager
        {
            void GetSensorsByCategory();
            ISensorCollection GetSensorsByType(ref Guid sensorType);
        }

        [ComImport, Guid("23571E11-E545-4DD8-A337-B89BF44B10DF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISensorCollection
        {
            ISensor GetAt(uint index);
            uint GetCount();
        }

        [ComImport, Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISensor
        {
            void GetID();
            void GetCategory();
            void GetType();
            [return: MarshalAs(UnmanagedType.BStr)] string GetFriendlyName();
            void GetProperty();
            void GetProperties();
            void GetSupportedDataFields();
            void SetProperties();
            void SupportsDataField();
            void GetState();
            ISensorDataReport GetData();
        }

        [ComImport, Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISensorDataReport
        {
            void GetTimestamp();
            void GetSensorValue(ref PropertyKey key, out PropVariant value);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public float fltVal;
        }

        private static Guid typeAmbientLight = new("97F115C8-599A-4153-8894-D2D12899918A");
        private static PropertyKey lightLux = new() { fmtid = new("E4C77CE2-DCB7-46E9-8439-4FEC548833A6"), pid = 2 };

        private static readonly object Sync = new();
        private static readonly System.Timers.Timer Timer = new(2000);
        private static ISensor? _sensor;
        private static int _lastLevel = -1;
        private static int _pendingLevel = -1;

        static AmbientLight() => Timer.Elapsed += OnElapsed;

        private static ISensor? OpenSensor()
        {
            var manager = (ISensorManager)new SensorManager();
            try
            {
                ISensorCollection sensors = manager.GetSensorsByType(ref typeAmbientLight);
                try { return sensors.GetCount() > 0 ? sensors.GetAt(0) : null; }
                finally { Marshal.ReleaseComObject(sensors); }
            }
            finally { Marshal.ReleaseComObject(manager); }
        }

        public static bool IsSupported()
        {
            if (Simulated.IsActive) return false;
            try
            {
                ISensor? sensor = OpenSensor();
                if (sensor is null) return false;
                Marshal.ReleaseComObject(sensor);
                return true;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return false; }
        }

        public static void ApplySetting()
        {
            lock (Sync)
            {
                if (AppConfig.Is("backlight_ambient") && !Simulated.IsActive)
                {
                    if (!Timer.Enabled && IsSupported()) Timer.Start();
                    return;
                }

                Stop();
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                Timer.Stop();
                ResetLevels();
                ReleaseSensor();
            }
        }

        public static void Reset()
        {
            lock (Sync) ResetLevels();
        }

        private static void ResetLevels() => _lastLevel = _pendingLevel = -1;

        private static void ReleaseSensor()
        {
            if (_sensor is null) return;
            Marshal.ReleaseComObject(_sensor);
            _sensor = null;
        }

        private static float GetLux()
        {
            try
            {
                _sensor ??= OpenSensor();
                if (_sensor is null) return -1;

                ISensorDataReport report = _sensor.GetData();
                try
                {
                    report.GetSensorValue(ref lightLux, out PropVariant value);
                    return value.vt == 4 ? value.fltVal : -1; // VT_R4
                }
                finally { Marshal.ReleaseComObject(report); }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                ReleaseSensor();
                return -1;
            }
        }

        private static void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            lock (Sync)
            {
                if (!Timer.Enabled || InputDispatcher.lidClose || InputDispatcher.tentMode || !InputDispatcher.backlightActivity)
                    return;

                float lux = GetLux();
                if (!float.IsFinite(lux) || lux < 0) return;

                int threshold = Math.Clamp(AppConfig.Get("backlight_ambient_lux", 10), 1, 400);
                int level = lux < threshold * 15 / 400f ? 3
                    : lux < threshold / 4f ? 2
                    : lux < threshold ? 1
                    : 0;
                level = Math.Min(level, InputDispatcher.GetBacklight());

                if (level == _lastLevel) { _pendingLevel = -1; return; }
                if (level != _pendingLevel) { _pendingLevel = level; return; }

                _lastLevel = level;
                _pendingLevel = -1;
                Aura.ApplyBrightness(level, "Ambient");
            }
        }
    }
}
