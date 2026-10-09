using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvestigationNightmares.Formats;

/// <summary>One file inside a DW_PACK archive. Path uses forward slashes.</summary>
public sealed record DwPackEntry(string Path, int CompressedSize, int UncompressedSize, bool Compressed, long AbsoluteOffset);

/// <summary>
/// Reader for Persona 4 Golden PC's PreApp Partners "DW_PACK" archives (data00000.pac ...).
/// Header (0x14): "DW_PACK\0", int field08, int fileCount, int packIndex.
/// Entries (0x120 each): int, short index, short packIndex, char[260] path (Shift-JIS),
/// int (0x10C), int compressedSize (0x110), int uncompressedSize, int flags (1 = Huffman), int dataOffset (0x11C).
/// Data begins after the entry table; dataOffset is relative to that.
/// Only the requested entries are read, so the multi-GB archives are never loaded whole.
/// </summary>
public sealed class DwPack : IDisposable
{
    public const int HeaderSize = 0x14;
    public const int EntrySize = 0x120;
    public static ReadOnlySpan<byte> Signature => "DW_PACK\0"u8;

    readonly Stream _stream;
    readonly bool _owns;
    public IReadOnlyList<DwPackEntry> Entries { get; }
    public int PackIndex { get; }

    static Encoding PathEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
    }

    public DwPack(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _owns = ownsStream;
        Span<byte> hdr = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(hdr);
        if (!hdr[..8].SequenceEqual(Signature)) throw new InvalidDataException("not a DW_PACK archive");
        int count = BinaryPrimitives.ReadInt32LittleEndian(hdr[12..]);
        PackIndex = BinaryPrimitives.ReadInt32LittleEndian(hdr[16..]);
        if (count < 0 || count > 1_000_000) throw new InvalidDataException($"bad DW_PACK file count {count}");

        var table = new byte[count * EntrySize];
        stream.ReadExactly(table);
        long dataStart = HeaderSize + (long)count * EntrySize;
        var enc = PathEncoding;
        var list = new List<DwPackEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * EntrySize, EntrySize);
            var pathBytes = e.Slice(8, 260);
            int nul = pathBytes.IndexOf((byte)0);
            string path = enc.GetString(nul < 0 ? pathBytes : pathBytes[..nul]).Replace('\\', '/');
            int csize = BinaryPrimitives.ReadInt32LittleEndian(e[0x110..]);
            int usize = BinaryPrimitives.ReadInt32LittleEndian(e[0x114..]);
            int flags = BinaryPrimitives.ReadInt32LittleEndian(e[0x118..]);
            int off = BinaryPrimitives.ReadInt32LittleEndian(e[0x11C..]);
            list.Add(new DwPackEntry(path, csize, usize, flags > 0, dataStart + off));
        }
        Entries = list;
    }

    public static DwPack Open(string path) => new(File.OpenRead(path));

    public byte[] Read(DwPackEntry entry)
    {
        var raw = new byte[entry.CompressedSize];
        lock (_stream)
        {
            _stream.Position = entry.AbsoluteOffset;
            _stream.ReadExactly(raw);
        }
        return entry.Compressed ? PreappHuffman.Decompress(raw, entry.UncompressedSize) : raw;
    }

    public void Dispose()
    {
        if (_owns) _stream.Dispose();
    }

    /// <summary>Build an archive (tests, and repacking tools).</summary>
    public static byte[] Build(IEnumerable<(string path, byte[] data)> files, bool compress, int packIndex = 0)
    {
        var items = new List<(string path, byte[] stored, int usize)>();
        foreach (var (p, d) in files) items.Add((p, compress ? PreappHuffman.Compress(d) : d, d.Length));
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(Signature);
        bw.Write(0); bw.Write(items.Count); bw.Write(packIndex);
        var enc = PathEncoding;
        int dataOff = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bw.Write(0); bw.Write((short)i); bw.Write((short)packIndex);
            var pb = new byte[260];
            enc.GetBytes(items[i].path.Replace('/', '\\')).CopyTo(pb, 0);
            bw.Write(pb);
            bw.Write(0); bw.Write(items[i].stored.Length); bw.Write(items[i].usize);
            bw.Write(compress ? 1 : 0); bw.Write(dataOff);
            dataOff += items[i].stored.Length;
        }
        foreach (var it in items) bw.Write(it.stored);
        return ms.ToArray();
    }
}
