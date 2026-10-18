using System.Buffers.Binary;
using System.IO.Compression;

namespace Chip.Core.Scene;

/// <summary>Image headers (PNG, JPEG, TGA) and the small amount of pixel work export needs (TGA → PNG).</summary>
public sealed partial class MaterialLibrary
{
    /// <summary>Reads an image's pixel size from its header. PNG and JPEG are detected by signature, TGA by extension.</summary>
    public static bool TryReadImageSize(string file, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = File.OpenRead(file);
            return TryReadImageSize(fs, System.IO.Path.GetExtension(file), out width, out height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Reads an image's pixel size from the start of <paramref name="s"/>. <paramref name="extension"/> enables TGA (it has no signature).</summary>
    public static bool TryReadImageSize(Stream s, string extension, out int width, out int height)
    {
        width = height = 0;
        Span<byte> head = stackalloc byte[24];
        int n = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (n >= 24 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
        {
            width = BinaryPrimitives.ReadInt32BigEndian(head[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(head[20..]);
        }
        else if (n >= 4 && head[0] == 0xFF && head[1] == 0xD8)
        {
            s.Position = 2;
            if (!TryReadJpegSize(s, out width, out height)) return false;
        }
        else if (n >= 18 && string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase) && IsTgaHeader(head))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(head[12..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(head[14..]);
        }
        else return false;
        return width > 0 && height > 0;
    }

    /// <summary>Walks JPEG segments to the first start-of-frame marker (baseline, progressive, lossless...).</summary>
    static bool TryReadJpegSize(Stream s, out int width, out int height)
    {
        width = height = 0;
        Span<byte> seg = stackalloc byte[7];
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return false;
            if (b != 0xFF) continue;
            int marker;
            do marker = s.ReadByte(); while (marker == 0xFF);
            if (marker < 0) return false;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) continue; // standalone markers carry no length
            if (marker == 0xD9 || marker == 0xDA) return false;     // end of image / start of scan before any frame
            if (s.ReadAtLeast(seg[..2], 2, false) < 2) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(seg);
            if (length < 2) return false;
            bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (sof)
            {
                if (s.ReadAtLeast(seg[2..7], 5, false) < 5) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(seg[3..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(seg[5..]);
                return true;
            }
            s.Seek(length - 2, SeekOrigin.Current);
        }
    }

    static bool IsTgaHeader(ReadOnlySpan<byte> h) =>
        h[1] is 0 or 1 && h[2] is 1 or 2 or 3 or 9 or 10 or 11 && h[16] is 8 or 15 or 16 or 24 or 32;

    /// <summary>
    /// Image bytes to embed in an export: PNG and JPEG as-is, TGA converted to PNG (glTF has no TGA).
    /// Null when the file is missing or can't be converted.
    /// </summary>
    public static (byte[] Data, string MimeType)? ExportImage(MaterialInfo info)
    {
        if (info.Path == null || !File.Exists(info.Path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(info.Path);
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 'P') return (bytes, "image/png");
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) return (bytes, "image/jpeg");
            if (TryDecodeTga(bytes, out int w, out int h, out var rgba)) return (EncodePng(w, h, rgba), "image/png");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Decodes an uncompressed or RLE true-colour/greyscale TGA (8/15/16/24/32 bpp) to top-down RGBA8.</summary>
    public static bool TryDecodeTga(byte[] file, out int width, out int height, out byte[] rgba)
    {
        width = height = 0;
        rgba = [];
        if (file.Length < 18 || !IsTgaHeader(file)) return false;
        int type = file[2], bpp = file[16], desc = file[17];
        if (type is 1 or 9) return false; // colour-mapped: not supported
        width = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12));
        height = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14));
        if (width == 0 || height == 0) return false;
        int bytesPer = (bpp + 7) / 8;
        int pos = 18 + file[0] + (file[1] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(5)) * ((file[7] + 7) / 8) : 0);
        int count = width * height;
        var px = new byte[count * 4];
        bool rle = type >= 9;
        int i = 0;
        while (i < count)
        {
            int run = 1;
            bool repeat = false;
            if (rle)
            {
                if (pos >= file.Length) return false;
                int head = file[pos++];
                run = (head & 0x7F) + 1;
                repeat = (head & 0x80) != 0;
            }
            for (int k = 0; k < run && i < count; k++, i++)
            {
                if (pos + bytesPer > file.Length) return false;
                ReadTgaPixel(file.AsSpan(pos, bytesPer), bpp, px.AsSpan(i * 4, 4));
                if (!repeat || k == run - 1) pos += bytesPer;
            }
        }
        if ((desc & 0x20) == 0) // bottom-up: flip to top-down
        {
            int stride = width * 4;
            var row = new byte[stride];
            for (int y = 0; y < height / 2; y++)
            {
                var a = px.AsSpan(y * stride, stride);
                var b = px.AsSpan((height - 1 - y) * stride, stride);
                a.CopyTo(row);
                b.CopyTo(a);
                row.CopyTo(b);
            }
        }
        rgba = px;
        return true;
    }

    static void ReadTgaPixel(ReadOnlySpan<byte> s, int bpp, Span<byte> d)
    {
        switch (bpp)
        {
            case 8: d[0] = d[1] = d[2] = s[0]; d[3] = 255; break;
            case 15 or 16:
                int v = s[0] | (s[1] << 8);
                d[0] = (byte)(((v >> 10) & 31) * 255 / 31);
                d[1] = (byte)(((v >> 5) & 31) * 255 / 31);
                d[2] = (byte)((v & 31) * 255 / 31);
                d[3] = 255;
                break;
            case 24: d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = 255; break;
            default: d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3]; break;
        }
    }

    /// <summary>Encodes top-down RGBA8 pixels as a PNG (no filtering, zlib-compressed).</summary>
    public static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++) Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);

        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        var typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(b4, Crc32(typed));
        s.Write(b4);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
