using Arsenal.Helpers;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

internal sealed class EncodedVideoFrame
{
    internal byte[] Data = Array.Empty<byte>();
    internal int Length;
    internal RemoteDesktopProtocol.VideoFlags Flags;
    internal long TimestampUs;
}

internal interface IVideoEncoder : IDisposable
{
    /// <summary>What the phone should decode this with. Matched against its own list.</summary>
    string Codec { get; }

    int Width { get; }
    int Height { get; }

    /// <summary>Send a self contained picture on the next frame, whatever changed.</summary>
    void RequestKeyFrame();

    /// <summary>
    /// Encodes one captured surface.
    /// </summary>
    /// <returns>
    /// False when there is nothing to send, which on a still desktop is almost every
    /// tick and is the whole reason this path is cheap to leave running.
    /// </returns>
    bool TryEncode(CapturedFrame frame, EncodedVideoFrame encoded);
}

/// <summary>
/// Sends the parts of the screen that changed, as JPEG.
/// </summary>
/// <remarks>
/// A desktop is mostly still. Somebody reading a page changes a caret and a scrollbar;
/// somebody typing changes one line. Coding the whole surface sixty times a second to
/// carry that is what makes a naive remote desktop unusable on anything but a wire, so
/// the surface is divided into tiles, each tile is hashed, and only the ones whose hash
/// moved are coded.
///
/// <para>Adjacent changed tiles in the same row are merged into one rectangle before
/// coding. Twenty small JPEGs cost far more in per-image overhead than one wide strip
/// covering the same pixels, and text dragged across a window changes exactly that
/// shape.</para>
///
/// <para>This is the path that always works: no graphics device, no encoder MFT, no
/// driver. It costs more bandwidth than H.264 would on video content, which is why the
/// quality presets exist and why the tile size is tuned rather than guessed.</para>
/// </remarks>
internal sealed class JpegTileEncoder : IVideoEncoder
{
    /// <summary>
    /// Tile edge, in pixels.
    /// </summary>
    /// <remarks>
    /// Smaller tiles track a caret more tightly and waste fewer pixels per change;
    /// larger ones cost less to hash and produce fewer images. 128 sits where the two
    /// cross for a text-heavy desktop: a 2560x1600 surface is 320 tiles, which hashes in
    /// well under a millisecond, and a changed line of text touches two or three.
    /// </remarks>
    private const int TileSize = 128;

    /// <summary>
    /// Above this share of changed tiles, code the whole surface instead.
    /// </summary>
    /// <remarks>
    /// Once most of the screen has moved, the tile list is pure overhead: rectangle
    /// headers, repeated JPEG tables, and a boundary every 128 pixels that the encoder
    /// cannot predict across. A scroll, a window drag or a video crosses this every frame.
    /// </remarks>
    private const double FullFrameThreshold = 0.6;

    private readonly ImageCodecInfo _jpeg;
    private readonly EncoderParameters _parameters;
    private readonly int _columns;
    private readonly int _rows;
    private readonly ulong[] _hashes;
    private readonly bool[] _changed;
    private readonly MemoryStream _scratch = new(256 * 1024);
    private byte[] _output = new byte[512 * 1024];
    private bool _keyFrameWanted = true;
    private bool _disposed;

    internal JpegTileEncoder(int width, int height, int quality)
    {
        Width = width;
        Height = height;
        _jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        _parameters = new EncoderParameters(1);
        _parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 20, 95));

        _columns = (width + TileSize - 1) / TileSize;
        _rows = (height + TileSize - 1) / TileSize;
        _hashes = new ulong[_columns * _rows];
        _changed = new bool[_columns * _rows];
    }

    public string Codec => "jpeg-tiles";
    public int Width { get; }
    public int Height { get; }

    public void RequestKeyFrame() => _keyFrameWanted = true;

    public bool TryEncode(CapturedFrame frame, EncodedVideoFrame encoded)
    {
        if (_disposed || frame.Width != Width || frame.Height != Height) return false;

        int changedCount = MarkChangedTiles(frame);
        bool keyFrame = _keyFrameWanted;
        if (changedCount == 0 && !keyFrame) return false;

        _keyFrameWanted = false;
        encoded.TimestampUs = frame.TimestampUs;

        if (keyFrame || changedCount > _changed.Length * FullFrameThreshold)
        {
            return EncodeFull(frame, encoded);
        }
        return EncodeTiles(frame, encoded);
    }

    /// <summary>
    /// Hashes every tile and records the ones that moved.
    /// </summary>
    /// <remarks>
    /// FNV-1a over the tile's rows, eight bytes at a time. A hash rather than a compare
    /// because the previous surface would otherwise have to be kept in full, doubling the
    /// memory a session holds at 4K for no gain: a collision costs one stale tile until
    /// the next keyframe, and at 64 bits over a 128x128 tile that is not a risk worth
    /// paying twenty megabytes to remove.
    /// </remarks>
    private int MarkChangedTiles(CapturedFrame frame)
    {
        int changed = 0;
        var pixels = frame.Pixels.AsSpan(0, frame.Stride * frame.Height);

        for (int row = 0; row < _rows; row++)
        {
            int top = row * TileSize;
            int bottom = Math.Min(top + TileSize, Height);
            for (int column = 0; column < _columns; column++)
            {
                int left = column * TileSize;
                int right = Math.Min(left + TileSize, Width);
                int widthBytes = (right - left) * 4;

                ulong hash = 14695981039346656037UL;
                for (int y = top; y < bottom; y++)
                {
                    var scanline = pixels.Slice(y * frame.Stride + left * 4, widthBytes);
                    int aligned = widthBytes & ~7;
                    foreach (ulong value in MemoryMarshal.Cast<byte, ulong>(scanline[..aligned]))
                    {
                        hash = (hash ^ value) * 1099511628211UL;
                    }
                    // The right-hand column of tiles is rarely a multiple of eight bytes
                    // wide. Skipping the remainder would leave up to one pixel column per
                    // row unwatched, and a caret sitting in it would not redraw until the
                    // next keyframe.
                    for (int i = aligned; i < widthBytes; i++)
                    {
                        hash = (hash ^ scanline[i]) * 1099511628211UL;
                    }
                }

                int index = row * _columns + column;
                bool moved = _hashes[index] != hash;
                _hashes[index] = hash;
                _changed[index] = moved;
                if (moved) changed++;
            }
        }
        return changed;
    }

    private bool EncodeFull(CapturedFrame frame, EncodedVideoFrame encoded)
    {
        byte[]? jpeg = Compress(frame, 0, 0, Width, Height);
        if (jpeg is null) return false;

        // [u64 pts][u16 width][u16 height][jpeg]
        int length = 8 + 2 + 2 + jpeg.Length;
        EnsureCapacity(length);
        var span = _output.AsSpan();
        BinaryPrimitives.WriteInt64BigEndian(span, frame.TimestampUs);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)Height);
        jpeg.CopyTo(span[12..]);

        encoded.Data = _output;
        encoded.Length = length;
        encoded.Flags = RemoteDesktopProtocol.VideoFlags.KeyFrame;
        return true;
    }

    private bool EncodeTiles(CapturedFrame frame, EncodedVideoFrame encoded)
    {
        var rectangles = MergeChangedRuns();
        if (rectangles.Count == 0) return false;

        // [u64 pts][u16 count] then per rectangle [u16 x][u16 y][u16 w][u16 h][u32 len][jpeg]
        int length = 10;
        var coded = new List<(int X, int Y, int W, int H, byte[] Jpeg)>(rectangles.Count);
        foreach (var (x, y, width, height) in rectangles)
        {
            byte[]? jpeg = Compress(frame, x, y, width, height);
            if (jpeg is null) continue;
            coded.Add((x, y, width, height, jpeg));
            length += 12 + jpeg.Length;
        }
        if (coded.Count == 0) return false;

        EnsureCapacity(length);
        var span = _output.AsSpan();
        BinaryPrimitives.WriteInt64BigEndian(span, frame.TimestampUs);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)coded.Count);
        int offset = 10;
        foreach (var (x, y, width, height, jpeg) in coded)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)x);
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], (ushort)y);
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 4)..], (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 6)..], (ushort)height);
            BinaryPrimitives.WriteInt32BigEndian(span[(offset + 8)..], jpeg.Length);
            jpeg.CopyTo(span[(offset + 12)..]);
            offset += 12 + jpeg.Length;
        }

        encoded.Data = _output;
        encoded.Length = offset;
        encoded.Flags = RemoteDesktopProtocol.VideoFlags.Tiles;
        return true;
    }

    /// <summary>Merges horizontally adjacent changed tiles into single rectangles.</summary>
    private List<(int X, int Y, int Width, int Height)> MergeChangedRuns()
    {
        var rectangles = new List<(int, int, int, int)>();
        for (int row = 0; row < _rows; row++)
        {
            int column = 0;
            while (column < _columns)
            {
                if (!_changed[row * _columns + column]) { column++; continue; }
                int start = column;
                while (column < _columns && _changed[row * _columns + column]) column++;

                int x = start * TileSize;
                int y = row * TileSize;
                int width = Math.Min(column * TileSize, Width) - x;
                int height = Math.Min((row + 1) * TileSize, Height) - y;
                if (width > 0 && height > 0) rectangles.Add((x, y, width, height));
            }
        }
        return rectangles;
    }

    /// <summary>
    /// Compresses one rectangle of the captured surface.
    /// </summary>
    /// <remarks>
    /// The Bitmap is constructed over the captured bytes rather than copied into: the
    /// surface is already 32-bit BGRA top-down, which is exactly PixelFormat.Format32bppRgb
    /// with a positive stride, so GDI+ can read the region in place.
    /// </remarks>
    private byte[]? Compress(CapturedFrame frame, int x, int y, int width, int height)
    {
        try
        {
            var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            try
            {
                IntPtr origin = handle.AddrOfPinnedObject() + y * frame.Stride + x * 4;
                using var region = new Bitmap(width, height, frame.Stride, PixelFormat.Format32bppRgb, origin);
                _scratch.SetLength(0);
                region.Save(_scratch, _jpeg, _parameters);
                return _scratch.ToArray();
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote encode: " + ex.Message);
            return null;
        }
    }

    private void EnsureCapacity(int length)
    {
        if (_output.Length >= length) return;
        _output = new byte[Math.Max(length, _output.Length * 2)];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parameters.Dispose();
        _scratch.Dispose();
    }
}
