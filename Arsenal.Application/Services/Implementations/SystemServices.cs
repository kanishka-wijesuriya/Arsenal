using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Fan;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.USB;
using Arsenal.AutoUpdate;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Arsenal.Application.Services.Implementations
{
    public class DeviceStateService : IDeviceStateService
    {
        /// <summary>
        /// How often a visible surface is refreshed. Fan, temperature and power
        /// readings move slowly enough that this is a presentation cadence, not a
        /// sampling requirement.
        /// </summary>
        private const double VisibleIntervalMs = 1000;

        private readonly System.Timers.Timer _telemetryTimer;
        private HardwareTelemetry _currentTelemetry = new();
        private int _pollInProgress;
        private volatile bool _publishUpdates;

        public HardwareTelemetry CurrentTelemetry => _currentTelemetry;
        public event Action<HardwareTelemetry>? TelemetryUpdated;

        public DeviceStateService()
        {
            // The dashboard is a permanent consumer of the original sensor loop.
            // Keep the fan, usage, power and battery channels active so homepage
            // telemetry reflects the same live readings as the legacy overlay.
            HardwareControl.readFans = true;
            HardwareControl.readUsage = true;
            HardwareControl.readPower = true;
            HardwareControl.readBattery = true;

            // Stopped until something is actually looking. One sample is taken now so
            // the first surface to open has a value to draw rather than a blank card.
            _telemetryTimer = new System.Timers.Timer(VisibleIntervalMs) { AutoReset = true };
            _telemetryTimer.Elapsed += (s, e) => PollHardware();
            ThreadPool.QueueUserWorkItem(_ => PollHardware());
        }

        public void SetPollingInterval(TimeSpan interval)
        {
            _telemetryTimer.Interval = Math.Max(500, interval.TotalMilliseconds);
        }

        /// <summary>
        /// Stops sampling. Called whenever no window is on screen.
        ///
        /// Nothing consumes telemetry in that state, which is why this stops rather
        /// than slows down. The phone companion calls <see cref="RefreshNow"/> itself
        /// immediately before answering a request, and the in-game overlay reads the
        /// sensors directly, so neither depends on this timer. A background poll here
        /// was a complete ACPI, GPU and power read - the most expensive thing the
        /// application does - whose result was then thrown away.
        /// </summary>
        public void PausePolling()
        {
            _publishUpdates = false;
            _telemetryTimer.Stop();
        }

        public void ResumePolling()
        {
            bool wasPublishing = _publishUpdates;
            _publishUpdates = true;
            _telemetryTimer.Interval = VisibleIntervalMs;
            _telemetryTimer.Start();

            if (wasPublishing) return;

            // Draw the last known sample at once so the page is never blank, then
            // replace it with a fresh one without waiting out a whole interval.
            TelemetryUpdated?.Invoke(_currentTelemetry);
            ThreadPool.QueueUserWorkItem(_ => PollHardware());
        }

        public void RefreshNow() => PollHardware();

        private void PollHardware()
        {
            // System.Timers.Timer is re-entrant. Sensor access includes ACPI, GPU and
            // power providers, so a slow sample must never overlap the next one and
            // multiply both CPU usage and native handles.
            if (Interlocked.Exchange(ref _pollInProgress, 1) != 0) return;
            try
            {
                // The optional legacy overlay can reset these shared flags when it
                // closes. Reassert them on every dashboard poll so RPM, usage and
                // power never become permanently null/zero after an overlay state
                // change. ReadSensorsOverlay is the original path that populates
                // HardwareControl.gpuPower from NVML/ADL.
                HardwareControl.readFans = true;
                HardwareControl.readUsage = true;
                HardwareControl.readPower = true;
                HardwareControl.readBattery = true;
                HardwareControl.ReadSensorsOverlay();

                float cpuTemp = HardwareControl.cpuTemp ?? 0;
                float cpuUsage = (float)(HardwareControl.cpuUsage ?? 0);
                float cpuPower = HardwareControl.cpuPower ?? 0;

                float gpuTemp = HardwareControl.gpuTemp ?? 0;
                float gpuUsage = (float)(HardwareControl.gpuUsage ?? 0);
                float gpuPower = HardwareControl.gpuPower ?? 0;

                int fanCpu = HardwareControl.cpuFanRPM ?? 0;
                int fanGpu = HardwareControl.gpuFanRPM ?? 0;
                int fanMid = 0;
                int fanXgm = 0;

                int batteryPercent = (int)Math.Round((float)System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);
                float dischargeRate = (float)(HardwareControl.batteryRate ?? 0);
                bool isAc = Program.currentSource != Program.PowerSource.Battery;

                string gpuStatus = GPUModeControl.gpuMode switch
                {
                    AsusACPI.GPUModeEco => "Eco (Disabled)",
                    AsusACPI.GPUModeStandard => "Standard (Active)",
                    AsusACPI.GPUModeUltimate => "Ultimate (dGPU Direct)",
                    _ => "Optimized"
                };

                _currentTelemetry = new HardwareTelemetry(
                    CpuTemp: cpuTemp,
                    CpuUsage: cpuUsage,
                    CpuPower: cpuPower,
                    GpuTemp: gpuTemp,
                    GpuUsage: gpuUsage,
                    GpuPower: gpuPower,
                    FanCpuRpm: fanCpu,
                    FanGpuRpm: fanGpu,
                    FanMidRpm: fanMid,
                    FanXgmRpm: fanXgm,
                    BatteryPercentage: batteryPercent,
                    BatteryDischargeRate: dischargeRate,
                    IsAcConnected: isAc,
                    GpuStatus: gpuStatus
                );

                if (_publishUpdates) TelemetryUpdated?.Invoke(_currentTelemetry);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Telemetry error: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _pollInProgress, 0);
            }
        }

        public void Dispose()
        {
            _telemetryTimer.Stop();
            _telemetryTimer.Dispose();
        }
    }

    public class UpdateService : IUpdateService
    {
        private static readonly HttpClient AsusClient = CreateClient("Arsenal App", decompress: true);

        /// <summary>
        /// Separate from <see cref="AsusClient"/> for two reasons: a driver package can
        /// take far longer than the thirty seconds a JSON call is allowed - the caller's
        /// cancellation token is the only limit that makes sense for it - and automatic
        /// decompression would make Content-Length describe the compressed size while the
        /// stream yields the uncompressed one, which would report nonsense progress.
        /// </summary>
        private static readonly HttpClient DownloadClient = CreateDownloadClient();
        private readonly ReleaseFeedClient _releaseFeed = new();
        private ReleaseUpdate? _pendingRelease;

        public event Action<UpdateInfo>? UpdateStatusChanged;

        public async Task<UpdateInfo> CheckForUpdatesAsync(bool force = false)
        {
            var info = new UpdateInfo
            {
                CurrentVersion = ReleaseVersion.CurrentString(),
                IsUpdateAvailable = false
            };

            try
            {
                ReleaseUpdate release = await _releaseFeed.FetchLatestAsync();
                _pendingRelease = release;
                info.LatestVersion = release.Version;
                info.Title = release.Title;
                info.ReleaseNotes = string.Join(Environment.NewLine, release.Notes.Select(note => "• " + note));
                info.ReleaseNoteLines = release.Notes.ToList();
                info.PackageBytes = release.PackageBytes;
                info.DownloadUrl = release.PackageUrl;
                info.PublishedAt = release.PublishedAt;
                info.IsUpdateAvailable = ReleaseVersion.Parse(release.Version).CompareTo(ReleaseVersion.Parse(info.CurrentVersion)) > 0;

                UpdateStatusChanged?.Invoke(info);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Update check error: " + ex.Message);
            }

            return info;
        }

        public async Task<bool> DownloadAndInstallUpdateAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            if (_pendingRelease is null) return false;
            return await DownloadAndInstallUpdateAsync(_pendingRelease, progress, cancellationToken);
        }

        public async Task<bool> DownloadAndInstallUpdateAsync(ReleaseUpdate release, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var updater = new AutoUpdateControl();
                return await updater.DownloadAndInstallAsync(release, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Update install error: " + ex.Message);
                return false;
            }
        }

        public async Task<List<UpdateInfo>> CheckAsusUpdatesAsync()
        {
            var (_, model) = AppConfig.GetBiosAndModel();
            string rogParam = AppConfig.IsROG() ? "&systemCode=rog" : string.Empty;
            string driversUrl = $"https://rog.asus.com/support/webapi/product/GetPDDrivers?website=global&model={Uri.EscapeDataString(model)}&cpu={Uri.EscapeDataString(model)}&osid=52{rogParam}";

            List<UpdateInfo> drivers = await FetchAsusUpdatesAsync(driversUrl);

            // Device inventory and the staged driver store are blocking Windows APIs.
            // Keep them off the UI thread while still resolving the complete list as
            // one snapshot, so each status comes from the same hardware state.
            await Task.Run(() => AsusDriverDetector.Resolve(drivers));

            return drivers
                .Where(item => !item.IsHidden)
                .OrderBy(u => u.SortRank)
                .ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task<List<UpdateInfo>> FetchAsusUpdatesAsync(string url)
        {
            var result = new List<UpdateInfo>();
            try
            {
                string json = await AsusClient.GetStringAsync(url);
                using var first = JsonDocument.Parse(json);
                bool missing = true;
                if (first.RootElement.TryGetProperty("Result", out JsonElement firstResult)
                    && firstResult.ValueKind == JsonValueKind.Object
                    && firstResult.TryGetProperty("Obj", out JsonElement firstGroups)
                    && firstGroups.ValueKind == JsonValueKind.Array)
                    missing = firstGroups.GetArrayLength() == 0;

                // ASUS occasionally returns an empty cached response. The legacy app's
                // cache-busting retry is retained because this is an API behaviour, not
                // a UI concern.
                if (missing)
                    json = await AsusClient.GetStringAsync(url + "&tag=" + Random.Shared.Next(10, 99));

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Result", out var apiResult) ||
                    !apiResult.TryGetProperty("Obj", out var groups)) return result;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] skip =
                {
                    "Armoury Crate & Aura Creator Installer", "MyASUS", "ASUS Smart Display Control",
                    "Aura Wallpaper", "Virtual Pet", "Virtual Pet- Ultimate Edition",
                    "Armoury Crate Control Interface", "Virtual Assistant", "GlideX",
                    "MyASUS in WinRE for USB"
                };
                foreach (var group in groups.EnumerateArray())
                {
                    string category = group.TryGetProperty("Name", out var name) ? name.GetString() ?? "ASUS" : "ASUS";
                    if (!group.TryGetProperty("Files", out var files)) continue;
                    foreach (var file in files.EnumerateArray())
                    {
                        string title = file.TryGetProperty("Title", out var titleProp) ? titleProp.GetString() ?? "ASUS update" : "ASUS update";
                        if (skip.Contains(title, StringComparer.OrdinalIgnoreCase)
                            || title.Contains(" Application", StringComparison.OrdinalIgnoreCase)
                            || title.Contains("Armoury Crate", StringComparison.OrdinalIgnoreCase)) continue;
                        string version = file.TryGetProperty("Version", out var versionProp) ? (versionProp.GetString() ?? "").TrimStart('V', 'v') : "";
                        if (title.Contains("Realtek LAN", StringComparison.OrdinalIgnoreCase))
                            title += " " + (Version.TryParse(version, out Version? parsed) ? parsed.Major : 0);
                        if (!seen.Add(title)) continue;
                        string download = file.TryGetProperty("DownloadUrl", out var urls) && urls.TryGetProperty("Global", out var global) ? global.GetString() ?? "" : "";
                        string date = file.TryGetProperty("ReleaseDate", out var dateProp) ? dateProp.GetString() ?? "" : "";
                        var hardwareIds = new List<string>();
                        if (file.TryGetProperty("HardwareInfoList", out var hardwareList) && hardwareList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement hardware in hardwareList.EnumerateArray())
                            {
                                if (!hardware.TryGetProperty("hardwareid", out JsonElement idElement)) continue;
                                string id = idElement.GetString() ?? string.Empty;
                                int revision = id.IndexOf("&REV_", StringComparison.OrdinalIgnoreCase);
                                if (revision >= 0) id = id[..revision];
                                if (!string.IsNullOrWhiteSpace(id)) hardwareIds.Add(id);
                            }
                        }
                        result.Add(new UpdateInfo
                        {
                            Title = title,
                            LatestVersion = version,
                            DownloadUrl = download,
                            PublishedAt = date,
                            ReleaseNotes = category,
                            FileSize = file.TryGetProperty("FileSize", out var size) ? size.GetString()?.Trim() ?? "" : "",
                            Sha256 = file.TryGetProperty("sha256", out var hash) ? hash.GetString()?.Trim() ?? "" : "",
                            HardwareIds = hardwareIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ASUS update check error: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Packages are written beside each other in one folder under Downloads, so a
        /// user who wants to keep or re-run an installer knows where to look.
        /// </summary>
        public string DownloadFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Arsenal Drivers");

        /// <summary>
        /// Hosts a package may be fetched from.
        /// </summary>
        /// <remarks>
        /// The URL comes out of a remote JSON document. While it only ever went to the
        /// browser that was the browser's problem; now that the application downloads it
        /// and offers to run it, a spoofed or tampered response must not be able to point
        /// this at an arbitrary executable. ASUS serves every package from its own CDN.
        /// </remarks>
        private static bool IsAsusHost(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.Equals("asus.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".asus.com", StringComparison.OrdinalIgnoreCase));

        public async Task<string?> DownloadAsusPackageAsync(
            string downloadUrl, IProgress<int>? progress, CancellationToken cancellationToken,
            string? expectedSha256 = null)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) || !IsAsusHost(uri))
            {
                Logger.WriteLine("Driver download refused, not an ASUS HTTPS address: " + downloadUrl);
                return null;
            }

            // The query carries a ?model= tag that is not part of the file name, and the
            // path segment is the only thing ASUS names the package by.
            string name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) name = "asus-driver.exe";
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');

            string folder = DownloadFolder;
            string target = Path.Combine(folder, name);
            // Written aside and moved into place at the end: a cancelled or broken
            // transfer must not leave a truncated file sitting there looking finished.
            string partial = target + ".partial";

            try
            {
                Directory.CreateDirectory(folder);

                using var response = await GetFollowingAsusRedirectsAsync(uri, cancellationToken);
                response.EnsureSuccessStatusCode();

                long? total = response.Content.Headers.ContentLength;
                progress?.Report(total is > 0 ? 0 : -1);

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(
                    partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    var buffer = new byte[128 * 1024];
                    long written = 0;
                    int lastReported = -1;
                    int read;

                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        written += read;

                        if (total is not > 0) continue;

                        // Only on a whole-percent change: the UI thread does not need a
                        // notification for every 128 KB of a 400 MB package.
                        int percent = (int)(written * 100 / total.Value);
                        if (percent == lastReported) continue;
                        lastReported = percent;
                        progress?.Report(percent);
                    }
                }

                if (!await MatchesAsync(partial, expectedSha256, cancellationToken))
                {
                    TryDelete(partial);
                    Logger.WriteLine($"Driver download rejected, SHA-256 mismatch: {name}");
                    return null;
                }

                File.Move(partial, target, overwrite: true);
                Logger.WriteLine($"Driver downloaded: {name}");
                return target;
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                return null;
            }
            catch (Exception ex)
            {
                TryDelete(partial);
                Logger.WriteLine($"Driver download failed ({name}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Whether the downloaded file is the one ASUS published.
        /// </summary>
        /// <remarks>
        /// True when no hash was supplied: the scan currently returns one for every
        /// package, but a missing hash is a gap in the catalogue rather than evidence
        /// against the file, and refusing every download over it would be worse than the
        /// behaviour this replaced. A hash that is present and wrong is another matter.
        /// </remarks>
        private static async Task<bool> MatchesAsync(string path, string? expected, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;

            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
                byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
                return Convert.ToHexString(hash).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Driver hash check: " + ex.Message);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.WriteLine("Driver download cleanup: " + ex.Message); }
        }

        private static HttpClient CreateClient(string userAgent, bool decompress = false)
        {
            HttpMessageHandler handler = decompress
                ? new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }
                : new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return client;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.None,

                // The allowlist check only ever saw the address the download started at.
                // With redirects followed automatically, a 302 off an ASUS host to
                // anywhere else was followed silently, and the file the app then offered
                // to run came from an address IsAsusHost never examined. Redirects are
                // now followed by hand, one hop at a time, so every address in the chain
                // is checked and not just the first.
                AllowAutoRedirect = false
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Arsenal App");
            return client;
        }

        /// <summary>
        /// Fetches <paramref name="uri"/>, following redirects only while every hop
        /// stays on an ASUS HTTPS host.
        /// </summary>
        /// <remarks>
        /// ASUS does redirect within its own CDN, so refusing redirects outright would
        /// break real downloads. Re-running the allowlist on each hop keeps those
        /// working while making a redirect off the CDN a hard failure rather than a
        /// silent one.
        /// </remarks>
        private static async Task<HttpResponseMessage> GetFollowingAsusRedirectsAsync(
            Uri uri, CancellationToken cancellationToken)
        {
            const int maxHops = 5;

            for (int hop = 0; ; hop++)
            {
                HttpResponseMessage response = await DownloadClient.GetAsync(
                    uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                bool redirected = response.StatusCode is System.Net.HttpStatusCode.Moved
                    or System.Net.HttpStatusCode.Found
                    or System.Net.HttpStatusCode.SeeOther
                    or System.Net.HttpStatusCode.TemporaryRedirect
                    or System.Net.HttpStatusCode.PermanentRedirect;

                if (!redirected) return response;

                Uri? next = response.Headers.Location;
                response.Dispose();

                if (next is null) throw new HttpRequestException("The ASUS download redirected without an address.");
                if (!next.IsAbsoluteUri) next = new Uri(uri, next);

                if (hop >= maxHops) throw new HttpRequestException("The ASUS download redirected too many times.");
                if (!IsAsusHost(next))
                {
                    Logger.WriteLine("Driver download refused, redirected off ASUS: " + next.AbsoluteUri);
                    throw new HttpRequestException("The ASUS download redirected to an address outside asus.com.");
                }

                uri = next;
            }
        }
    }

    public class ProfileService : IProfileService
    {
        public int AutoAcMode
        {
            get => AppConfig.Get("performance_1", AsusACPI.PerformanceBalanced);
            set => AppConfig.Set("performance_1", value);
        }

        public int AutoBatteryMode
        {
            get => AppConfig.Get("performance_0", AsusACPI.PerformanceSilent);
            set => AppConfig.Set("performance_0", value);
        }

        public bool IsAutoSwitchEnabled
        {
            get => AppConfig.IsNotFalse("auto_switch_enabled");
            set => AppConfig.Set("auto_switch_enabled", value ? 1 : 0);
        }

        public void OnPowerSourceChanged(bool isAc)
        {
            if (!IsAutoSwitchEnabled) return;
            int targetMode = isAc ? AutoAcMode : AutoBatteryMode;
            Program.modeControl?.SetPerformanceMode(targetMode, true);
        }

        public void OnAppForegroundChanged(string processName)
        {
            int profile = AppConfig.Get("app_profile_" + processName, -1);
            if (profile >= 0)
            {
                Program.modeControl?.SetPerformanceMode(profile, true);
            }
        }
    }

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
