using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Fan;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.USB;
using Arsenal.AutoUpdate;
using System.Reflection;
using System.Text.Json;

namespace Arsenal.Application.Services.Implementations
{
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
            // NUC drivers are listed under the full model name up to its SKU suffix,
            // not under the model the BIOS reports.
            if (AppConfig.ContainsModel("NUC"))
            {
                string full = AppConfig.GetModel();
                int trim = full.LastIndexOf('_');
                model = trim > 0 ? full[..trim] : full;
            }
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

        /// <summary>
        /// The local name a package is stored under.
        /// </summary>
        /// <remarks>
        /// Shared by the download and by the check that finds an earlier one, because a
        /// detector that derived the name even slightly differently would silently stop
        /// recognising finished downloads.
        ///
        /// The query carries a ?model= tag that is not part of the file name, and the
        /// path segment is the only thing ASUS names the package by.
        /// </remarks>
        private static string PackageFileName(Uri uri)
        {
            string name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) name = "asus-driver.exe";
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name;
        }

        /// <summary>
        /// The path of a package already sitting in the downloads folder, or null.
        /// </summary>
        /// <remarks>
        /// Download state lives on the driver record, which is rebuilt from scratch by
        /// every scan, so a package fetched before the application was closed was
        /// forgotten and the card offered to fetch it again. The file on disk is the
        /// durable part; this is how a fresh scan learns about it.
        ///
        /// Existence is the test, matching the check the download button already makes
        /// during a session. A `.partial` file is an interrupted transfer and is
        /// deliberately not accepted: only a completed download is renamed into place.
        /// </remarks>
        public string? FindDownloadedPackage(string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) || !IsAsusHost(uri)) return null;

            try
            {
                string path = Path.Combine(DownloadFolder, PackageFileName(uri));
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Could not check for an existing driver download: " + ex.Message);
                return null;
            }
        }

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

            string name = PackageFileName(uri);
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
}
