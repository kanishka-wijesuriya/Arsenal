using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Fan;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.USB;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Arsenal.Application.Services.Implementations
{
    public class SettingsSearchService : ISettingsSearchService
    {
        private readonly List<SearchItem> _items = new();

        public SettingsSearchService()
        {
            PopulateDefaultSearchIndex();
        }

        public void RegisterItem(SearchItem item)
        {
            _items.Add(item);
        }

        private const int MaxResults = 40;

        /// <summary>
        /// Scored search. The previous version was an unranked substring test, so the
        /// best match for "turbo" was whichever item happened to sit earliest in the
        /// index, and a typo or a word typed out of order found nothing at all.
        ///
        /// Every query term must match somewhere (AND), so extra words narrow rather
        /// than widen. Each term scores against the strongest field it hits, and the
        /// field weights mean a title match always outranks a description match.
        /// </summary>
        public IReadOnlyList<SearchItem> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return _items
                    .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxResults)
                    .ToList();
            }

            string[] terms = query.Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var matches = new List<SearchItem>();
            foreach (var item in _items)
            {
                int total = 0;
                bool everyTermMatched = true;

                foreach (var term in terms)
                {
                    int best = ScoreTerm(item, term);
                    if (best == 0) { everyTermMatched = false; break; }
                    total += best;
                }

                if (!everyTermMatched) continue;
                item.Relevance = total;
                matches.Add(item);
            }

            return matches
                .OrderByDescending(i => i.Relevance)
                .ThenBy(i => i.Title.Length)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .ToList();
        }

        private static int ScoreTerm(SearchItem item, string term)
        {
            // Weighted by how strongly a field identifies an item. A keyword hit counts
            // as much as a title hit: they exist precisely because the title does not
            // contain the word people search for.
            int score = FieldScore(item.Title, term) * 6;
            if (score == 0) score = item.Keywords.Max(k => FieldScore(k, term)) * 6;
            if (score == 0) score = FieldScore(item.Category, term) * 3;
            if (score == 0) score = FieldScore(item.Description, term) * 2;
            return score;
        }

        /// <summary>
        /// How well one field matches one term: exact, prefix, word-prefix, substring,
        /// then subsequence as a last resort so "ovrdrv" still finds Overdrive.
        /// </summary>
        private static int FieldScore(string field, string term)
        {
            if (string.IsNullOrEmpty(field)) return 0;
            string value = field.ToLowerInvariant();

            if (value == term) return 100;
            if (value.StartsWith(term, StringComparison.Ordinal)) return 70;

            foreach (var word in value.Split(' ', '-', '/', '(', ')', ','))
                if (word.StartsWith(term, StringComparison.Ordinal)) return 50;

            if (value.Contains(term, StringComparison.Ordinal)) return 30;
            return IsSubsequence(value, term) ? 10 : 0;
        }

        /// <summary>Every character of <paramref name="term"/> appears in order.</summary>
        private static bool IsSubsequence(string value, string term)
        {
            // Single letters would match nearly everything and turn the list to noise.
            if (term.Length < 3) return false;

            int at = 0;
            foreach (char c in value)
            {
                if (c != term[at]) continue;
                if (++at == term.Length) return true;
            }
            return false;
        }

        private void PopulateDefaultSearchIndex()
        {
            _items.Add(new SearchItem { Id = "mode_silent", Title = "Silent Mode", Description = "Quiet operation, low power consumption", Category = "Performance", PageTag = "Performance", Keywords = new[] { "quiet", "silent", "profile", "power profile" }, Action = () => Program.modeControl?.SetPerformanceMode(AsusACPI.PerformanceSilent, true) });
            _items.Add(new SearchItem { Id = "mode_balanced", Title = "Balanced Mode", Description = "Balanced performance and fan acoustics", Category = "Performance", PageTag = "Performance", Keywords = new[] { "balanced", "default", "profile", "power profile" }, Action = () => Program.modeControl?.SetPerformanceMode(AsusACPI.PerformanceBalanced, true) });
            _items.Add(new SearchItem { Id = "mode_turbo", Title = "Turbo Mode", Description = "Maximum sustained hardware performance", Category = "Performance", PageTag = "Performance", Keywords = new[] { "turbo", "performance", "max", "profile", "power profile" }, Action = () => Program.modeControl?.SetPerformanceMode(AsusACPI.PerformanceTurbo, true) });

            _items.Add(new SearchItem { Id = "gpu_eco", Title = "GPU Eco Mode", Description = "Disable dedicated GPU completely for battery life", Category = "Display & Graphics", PageTag = "Display", Keywords = new[] { "eco", "igpu", "integrated", "battery", "gpu" }, Action = () => Program.gpuControl?.SetGPUMode(AsusACPI.GPUModeEco) });
            _items.Add(new SearchItem { Id = "gpu_standard", Title = "GPU Standard Mode", Description = "Optimus hybrid GPU switching enabled", Category = "Display & Graphics", PageTag = "Display", Keywords = new[] { "standard", "hybrid", "optimus", "gpu" }, Action = () => Program.gpuControl?.SetGPUMode(AsusACPI.GPUModeStandard) });
            _items.Add(new SearchItem { Id = "gpu_ultimate", Title = "GPU Ultimate Mode (MUX)", Description = "Direct dedicated GPU connection (requires restart)", Category = "Display & Graphics", PageTag = "Display", Keywords = new[] { "ultimate", "mux", "dgpu", "discrete", "gpu" }, Action = () => Program.gpuControl?.SetGPUMode(AsusACPI.GPUModeUltimate) });

            _items.Add(new SearchItem { Id = "disp_refresh", Title = "Toggle Screen Refresh Rate", Description = "Switch between 60Hz and maximum refresh rate", Category = "Display & Graphics", PageTag = "Display", Keywords = new[] { "hz", "refresh", "60hz", "120hz", "144hz", "165hz", "240hz", "od", "overdrive", "display" }, Action = () => ScreenControl.ToggleScreenRate() });
            _items.Add(new SearchItem { Id = "disp_miniled", Title = "Toggle Mini-LED Multi-Zone", Description = "Switch between Single-Zone and Multi-Zone HDR backlight", Category = "Display & Graphics", PageTag = "Display", Keywords = new[] { "miniled", "mini-led", "hdr", "backlight", "zones", "display" }, Action = () => ScreenControl.ToogleMiniled() });

            _items.Add(new SearchItem { Id = "batt_limit_80", Title = "Set Battery Charge Limit to 80%", Description = "Preserve long term battery health", Category = "Battery", PageTag = "Battery", Keywords = new[] { "charge", "limit", "80", "battery health", "smart charging" }, Action = () => Battery.BatteryControl.SetBatteryChargeLimit(80) });
            _items.Add(new SearchItem { Id = "batt_full", Title = "Charge Battery to 100% (One-time)", Description = "Temporarily bypass limit to full charge", Category = "Battery", PageTag = "Battery", Keywords = new[] { "100", "full", "charge", "battery" }, Action = () => Battery.BatteryControl.ToggleBatteryLimitFull() });
            _items.Add(new SearchItem { Id = "batt_report", Title = "Generate Windows Battery Report", Description = "Detailed HTML report on battery cycle counts and wear", Category = "Battery", PageTag = "Battery", Keywords = new[] { "report", "health", "wear", "battery" }, Action = () => Battery.BatteryControl.BatteryReport() });

            _items.Add(new SearchItem { Id = "aura_cycle", Title = "Cycle Keyboard RGB Effect", Description = "Switch through Static, Breathing, Color Cycle, Rainbow", Category = "Lighting", PageTag = "Lighting", Keywords = new[] { "rgb", "aura", "lighting", "keyboard", "backlight", "led" }, Action = () => { int next = (AppConfig.Get("aura_mode", 0) + 1) % 4; AppConfig.Set("aura_mode", next); Aura.ApplyAura(); } });
            _items.Add(new SearchItem { Id = "fan_calibrate", Title = "Calibrate Fan Sensors", Description = "Detect maximum RPM ranges for all fans", Category = "Performance", PageTag = "Performance", Keywords = new[] { "fan", "calibrate", "rpm", "cooling" }, Action = () => new FanSensorControl().StartCalibration() });
            _items.Add(new SearchItem { Id = "asus_drivers", Title = "ASUS Drivers", Description = "Detect installed ASUS drivers and compare them with ASUS support", Category = "Drivers", PageTag = "Drivers", Keywords = new[] { "driver", "update", "installed", "asus", "support" } });
#if !ARSENAL_STORE
            _items.Add(new SearchItem { Id = "app_check_updates", Title = "Arsenal Updates", Description = "Check for a newer Arsenal application release", Category = "About", PageTag = "About", Keywords = new[] { "update", "version", "upgrade", "release" } });
#endif
            _items.Add(new SearchItem { Id = "mobile_companion", Title = "Mobile Companion", Description = "Pair phones, scan a QR code and manage connected devices", Category = "Mobile Companion", PageTag = "MobileCompanion", Keywords = new[] { "phone", "android", "mobile", "pair", "qr", "remote", "connected devices" } });
        }
    }
}
