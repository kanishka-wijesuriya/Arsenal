using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IUpdateService
    {
        event Action<UpdateInfo> UpdateStatusChanged;
        Task<UpdateInfo> CheckForUpdatesAsync(bool force = false);

        /// <summary>
        /// Downloads, verifies and starts installing the pending application update.
        /// <paramref name="progress"/> reports bytes received so the caller can show a
        /// real download bar; the signed byte count is on the <see cref="UpdateInfo"/>
        /// the check returned.
        /// </summary>
        Task<bool> DownloadAndInstallUpdateAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads and installs the exact signed release already presented by the
        /// startup checker. Unlike the parameterless overload, this path does not
        /// depend on a second service having populated private pending state.
        /// </summary>
        Task<bool> DownloadAndInstallUpdateAsync(ReleaseUpdate release, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default);
        Task<List<UpdateInfo>> CheckAsusUpdatesAsync();

        /// <summary>
        /// Fetches one ASUS driver package to disk and returns where it landed, or null
        /// if it did not arrive.
        /// </summary>
        /// <remarks>
        /// The alternative is handing the URL to the default browser, which leaves the
        /// user to find the file afterwards and gives the application no idea whether
        /// anything was downloaded. <paramref name="progress"/> reports percent complete,
        /// or -1 for as long as the server has not declared a length.
        /// </remarks>
        /// <param name="expectedSha256">
        /// ASUS's published hash for the package, when the scan returned one. The file is
        /// offered to the shell to run afterwards, so a package that does not match what
        /// the publisher says it should be is discarded rather than kept.
        /// </param>
        Task<string?> DownloadAsusPackageAsync(
            string downloadUrl, IProgress<int>? progress, CancellationToken cancellationToken,
            string? expectedSha256 = null);

        /// <summary>Folder the packages above are written to.</summary>
        string DownloadFolder { get; }

        /// <summary>
        /// The path of an already downloaded package for this URL, or null.
        /// </summary>
        /// <remarks>
        /// Download state is held on the driver record, and every scan rebuilds those
        /// from scratch, so a package fetched in an earlier run has to be rediscovered
        /// from the folder rather than remembered.
        /// </remarks>
        string? FindDownloadedPackage(string downloadUrl);
    }
}
