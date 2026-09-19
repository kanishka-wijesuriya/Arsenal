using Arsenal.Helpers;
using System.IO;
using System.Text.Json;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>Sends one framed message. Completes once the bytes have left for the socket.</summary>
internal interface IRemoteFrameWriter
{
    ValueTask SendAsync(RemoteDesktopProtocol.Channel channel, byte flags, ushort stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

/// <summary>
/// Browsing and moving files across the session.
/// </summary>
/// <remarks>
/// The whole filesystem, as the signed-in user sees it. That is the same access the
/// phone already has by driving the keyboard, so restricting it to a sandbox would buy
/// nothing and cost the one thing people actually reach for a remote session to do.
///
/// <para>Transfers are chunked and paced by the socket rather than queued: the send
/// awaits the write, so a large download naturally yields to video instead of
/// accumulating megabytes of file bytes in front of the next frame. Each transfer
/// carries an id in the frame's stream field, so several can run at once and any of them
/// can be cancelled on its own.</para>
///
/// <para>An upload lands on a temporary name beside its destination and is moved into
/// place at the end. A connection lost half way through then leaves a partial file that
/// is obviously partial, rather than a truncated file with the right name.</para>
/// </remarks>
internal sealed class RemoteFileService : IDisposable
{
    private const int ChunkSize = 192 * 1024;

    private readonly IRemoteFrameWriter _writer;
    private readonly bool _allowed;
    private readonly CancellationToken _sessionToken;
    private readonly Dictionary<ushort, CancellationTokenSource> _downloads = new();
    private readonly Dictionary<ushort, Upload> _uploads = new();
    private readonly object _gate = new();
    private bool _disposed;

    internal RemoteFileService(IRemoteFrameWriter writer, bool allowed, CancellationToken sessionToken)
    {
        _writer = writer;
        _allowed = allowed;
        _sessionToken = sessionToken;
    }

    private sealed record Upload(string Destination, string Temporary, FileStream Stream, long Expected)
    {
        internal long Written { get; set; }
    }

    internal async Task HandleAsync(byte flags, ushort stream, byte[] payload, int length)
    {
        if (_disposed) return;
        if (!_allowed)
        {
            await ErrorAsync(stream, "File transfer is turned off on this PC.");
            return;
        }

        try
        {
            if (flags == 1)
            {
                await ReceiveChunkAsync(stream, payload, length);
                return;
            }

            using JsonDocument document = JsonDocument.Parse(payload.AsMemory(0, length));
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() ?? string.Empty : string.Empty;

            switch (type)
            {
                case "roots": await SendRootsAsync(); break;
                case "list": await SendListingAsync(Text(root, "path")); break;
                case "download": await StartDownloadAsync(stream, Text(root, "path")); break;
                case "cancel": Cancel(stream); break;
                case "uploadStart": await StartUploadAsync(stream, Text(root, "path"), Number(root, "size")); break;
                case "uploadEnd": await FinishUploadAsync(stream); break;
                case "mkdir": await MakeDirectoryAsync(stream, Text(root, "path")); break;
                case "delete": await DeleteAsync(stream, Text(root, "path")); break;
                case "rename": await RenameAsync(stream, Text(root, "path"), Text(root, "name")); break;
                default: await ErrorAsync(stream, "Unsupported file request."); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote files: " + ex.Message);
            await ErrorAsync(stream, ex.Message);
        }
    }

    // ---- Browsing ----------------------------------------------------------------

    /// <summary>
    /// Where a person starts.
    /// </summary>
    /// <remarks>
    /// The known folders first, because that is where the file someone is reaching for
    /// almost always is, then the drives. A drive that is not ready is listed with its
    /// letter and no size rather than skipped: an empty card reader that vanishes from
    /// the list looks like a fault.
    /// </remarks>
    private async Task SendRootsAsync()
    {
        var entries = new List<object>();

        foreach ((string label, Environment.SpecialFolder folder) in new[]
        {
            ("Desktop", Environment.SpecialFolder.DesktopDirectory),
            ("Documents", Environment.SpecialFolder.MyDocuments),
            ("Downloads", Environment.SpecialFolder.UserProfile),
            ("Pictures", Environment.SpecialFolder.MyPictures),
            ("Videos", Environment.SpecialFolder.MyVideos),
            ("Music", Environment.SpecialFolder.MyMusic),
        })
        {
            string path = Environment.GetFolderPath(folder);
            if (label == "Downloads") path = Path.Combine(path, "Downloads");
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                entries.Add(new { name = label, path, directory = true, size = 0L, modified = 0L, kind = "folder" });
            }
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                entries.Add(new
                {
                    name = drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.VolumeLabel + " (" + drive.Name.TrimEnd('\\') + ")"
                        : drive.Name.TrimEnd('\\'),
                    path = drive.RootDirectory.FullName,
                    directory = true,
                    size = drive.IsReady ? drive.TotalSize : 0L,
                    free = drive.IsReady ? drive.AvailableFreeSpace : 0L,
                    modified = 0L,
                    kind = "drive",
                });
            }
            catch (Exception ex) { Logger.WriteLine("Remote files drive: " + ex.Message); }
        }

        await SendJsonAsync(0, new { type = "roots", entries });
    }

    private async Task SendListingAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { await SendRootsAsync(); return; }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            await ErrorAsync(0, "That folder is no longer there.");
            return;
        }

        var entries = new List<object>();
        foreach (FileSystemInfo item in Enumerate(directory))
        {
            bool isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
            entries.Add(new
            {
                name = item.Name,
                path = item.FullName,
                directory = isDirectory,
                size = isDirectory ? 0L : ((FileInfo)item).Length,
                modified = new DateTimeOffset(item.LastWriteTimeUtc).ToUnixTimeSeconds(),
                kind = isDirectory ? "folder" : "file",
            });
        }

        await SendJsonAsync(0, new
        {
            type = "listing",
            path = directory.FullName,
            parent = directory.Parent?.FullName,
            entries,
        });
    }

    /// <summary>
    /// Folders before files, each alphabetically, hidden and system entries left out.
    /// </summary>
    /// <remarks>
    /// Enumerated entry by entry so one unreadable folder does not fail the whole
    /// listing: a user profile directory contains several the signed-in user cannot open,
    /// and GetFileSystemInfos throws on the first of them.
    /// </remarks>
    private static IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };

        List<FileSystemInfo> items;
        try { items = directory.GetFileSystemInfos("*", options).ToList(); }
        catch (Exception ex) { Logger.WriteLine("Remote files list: " + ex.Message); return Array.Empty<FileSystemInfo>(); }

        return items
            .OrderByDescending(item => (item.Attributes & FileAttributes.Directory) != 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ---- Download ----------------------------------------------------------------

    private async Task StartDownloadAsync(ushort id, string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            await ErrorAsync(id, "That file is no longer there.");
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken);
        lock (_gate)
        {
            if (_downloads.TryGetValue(id, out CancellationTokenSource? existing)) existing.Cancel();
            _downloads[id] = cancellation;
        }

        await SendJsonAsync(id, new { type = "downloadStart", id, name = file.Name, size = file.Length });
        _ = PumpDownloadAsync(id, file, cancellation);
    }

    private async Task PumpDownloadAsync(ushort id, FileInfo file, CancellationTokenSource cancellation)
    {
        byte[] buffer = new byte[ChunkSize];
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ChunkSize, useAsync: true);
            while (!cancellation.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, cancellation.Token);
                if (read <= 0) break;

                // Awaiting the write is the flow control. Without it a fast disk fills
                // the send queue with file bytes and the screen stops moving.
                await _writer.SendAsync(RemoteDesktopProtocol.Channel.Files, 1, id, buffer.AsMemory(0, read), cancellation.Token);
            }

            if (!cancellation.IsCancellationRequested)
            {
                await SendJsonAsync(id, new { type = "downloadEnd", id });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote download: " + ex.Message);
            await ErrorAsync(id, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (_downloads.TryGetValue(id, out CancellationTokenSource? held) && ReferenceEquals(held, cancellation))
                {
                    _downloads.Remove(id);
                }
            }
            cancellation.Dispose();
        }
    }

    private void Cancel(ushort id)
    {
        lock (_gate)
        {
            if (_downloads.Remove(id, out CancellationTokenSource? download)) download.Cancel();
            if (_uploads.Remove(id, out Upload? upload)) AbandonUpload(upload);
        }
    }

    // ---- Upload ------------------------------------------------------------------

    private async Task StartUploadAsync(ushort id, string path, long size)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (!Directory.Exists(directory))
        {
            await ErrorAsync(id, "That folder is no longer there.");
            return;
        }

        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + ".arsenal-part");
        var upload = new Upload(path, temporary,
            new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true), size);

        lock (_gate)
        {
            if (_uploads.Remove(id, out Upload? existing)) AbandonUpload(existing);
            _uploads[id] = upload;
        }
        await SendJsonAsync(id, new { type = "uploadReady", id });
    }

    private async Task ReceiveChunkAsync(ushort id, byte[] payload, int length)
    {
        Upload? upload;
        lock (_gate) _uploads.TryGetValue(id, out upload);
        if (upload is null) return;

        await upload.Stream.WriteAsync(payload.AsMemory(0, length), _sessionToken);
        upload.Written += length;
    }

    private async Task FinishUploadAsync(ushort id)
    {
        Upload? upload;
        lock (_gate) _uploads.Remove(id, out upload);
        if (upload is null) return;

        try
        {
            await upload.Stream.FlushAsync(_sessionToken);
            await upload.Stream.DisposeAsync();
            File.Move(upload.Temporary, upload.Destination, overwrite: true);
            await SendJsonAsync(id, new { type = "uploadEnd", id, size = upload.Written });
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote upload: " + ex.Message);
            TryDelete(upload.Temporary);
            await ErrorAsync(id, ex.Message);
        }
    }

    private static void AbandonUpload(Upload upload)
    {
        try { upload.Stream.Dispose(); } catch { }
        TryDelete(upload.Temporary);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.WriteLine("Remote upload cleanup: " + ex.Message); }
    }

    // ---- Housekeeping ------------------------------------------------------------

    private async Task MakeDirectoryAsync(ushort id, string path)
    {
        Directory.CreateDirectory(path);
        await SendListingAsync(Path.GetDirectoryName(path) ?? path);
    }

    private async Task DeleteAsync(ushort id, string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        else { await ErrorAsync(id, "That item is no longer there."); return; }

        await SendListingAsync(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? path);
    }

    private async Task RenameAsync(ushort id, string path, string name)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await ErrorAsync(id, "That name cannot be used.");
            return;
        }

        string destination = Path.Combine(directory, name);
        if (Directory.Exists(path)) Directory.Move(path, destination);
        else File.Move(path, destination, overwrite: false);
        await SendListingAsync(directory);
    }

    // ---- Framing -----------------------------------------------------------------

    private ValueTask SendJsonAsync<T>(ushort stream, T message) =>
        _writer.SendAsync(RemoteDesktopProtocol.Channel.Files, 0, stream, RemoteDesktopProtocol.Encode(message), _sessionToken);

    private async Task ErrorAsync(ushort stream, string message)
    {
        try { await SendJsonAsync(stream, new { type = "error", id = stream, message }); }
        catch (Exception ex) { Logger.WriteLine("Remote files error report: " + ex.Message); }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static long Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long parsed) ? parsed : 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (CancellationTokenSource download in _downloads.Values)
            {
                download.Cancel();
                download.Dispose();
            }
            _downloads.Clear();
            foreach (Upload upload in _uploads.Values) AbandonUpload(upload);
            _uploads.Clear();
        }
    }
}
