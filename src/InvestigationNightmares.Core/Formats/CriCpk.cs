using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvestigationNightmares.Formats;

/// <summary>One file listed in a CRI CPK's table of contents.</summary>
public sealed record CpkEntry(string Path, long Offset, long StoredSize, long ExtractSize)
{
    public bool Compressed => ExtractSize > StoredSize;
}

/// <summary>
/// CRI Middleware "@UTF" tables: the row/column format CRI uses for CPK headers and tables of contents
/// and for audio banks (.csb/.acb). Big-endian. Columns: flags byte (storage 0x10 zero, 0x30 constant,
/// 0x50 per-row; type 0x0F), u32 name offset, constant value if storage is constant.
/// Tables may be XOR-obfuscated; <see cref="Read"/> undoes that.
/// </summary>
public sealed class UtfTable
{
    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<Dictionary<string, object?>> Rows { get; }

    UtfTable(string name, List<string> cols, List<Dictionary<string, object?>> rows) { Name = name; Columns = cols; Rows = rows; }

    public static bool LooksLikeUtf(ReadOnlySpan<byte> d) => d.Length >= 8 && (d[..4].SequenceEqual("@UTF"u8) || Deobfuscate(d[..4].ToArray()).AsSpan().SequenceEqual("@UTF"u8));

    static byte[] Deobfuscate(byte[] d)
    {
        var o = new byte[d.Length];
        uint m = 0x0000655F;
        for (int i = 0; i < d.Length; i++) { o[i] = (byte)(d[i] ^ (byte)m); m *= 0x00004115; }
        return o;
    }

    public static UtfTable Read(ReadOnlySpan<byte> input)
    {
        byte[] d = input[..4].SequenceEqual("@UTF"u8) ? input.ToArray() : Deobfuscate(input.ToArray());
        if (!d.AsSpan(0, 4).SequenceEqual("@UTF"u8)) throw new InvalidDataException("not a CRI @UTF table");
        int size = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4));
        var t = d.AsSpan(8, Math.Min(size, d.Length - 8));
        int rowsOff = BinaryPrimitives.ReadUInt16BigEndian(t[2..]);
        int strOff = BinaryPrimitives.ReadInt32BigEndian(t[4..]);
        int dataOff = BinaryPrimitives.ReadInt32BigEndian(t[8..]);
        int nameOff = BinaryPrimitives.ReadInt32BigEndian(t[12..]);
        int colCount = BinaryPrimitives.ReadUInt16BigEndian(t[16..]);
        int rowLen = BinaryPrimitives.ReadUInt16BigEndian(t[18..]);
        int rowCount = BinaryPrimitives.ReadInt32BigEndian(t[20..]);
        var tt = t.ToArray();

        string Str(int off)
        {
            int start = strOff + off;
            if (start < 0 || start >= tt.Length) return "";
            int end = Array.IndexOf(tt, (byte)0, start);
            return Encoding.UTF8.GetString(tt, start, (end < 0 ? tt.Length : end) - start);
        }

        object? Value(int type, ref int pos)
        {
            object? v;
            switch (type)
            {
                case 0x0: v = tt[pos]; pos += 1; break;
                case 0x1: v = (sbyte)tt[pos]; pos += 1; break;
                case 0x2: v = BinaryPrimitives.ReadUInt16BigEndian(tt.AsSpan(pos)); pos += 2; break;
                case 0x3: v = BinaryPrimitives.ReadInt16BigEndian(tt.AsSpan(pos)); pos += 2; break;
                case 0x4: v = BinaryPrimitives.ReadUInt32BigEndian(tt.AsSpan(pos)); pos += 4; break;
                case 0x5: v = BinaryPrimitives.ReadInt32BigEndian(tt.AsSpan(pos)); pos += 4; break;
                case 0x6: v = BinaryPrimitives.ReadUInt64BigEndian(tt.AsSpan(pos)); pos += 8; break;
                case 0x7: v = BinaryPrimitives.ReadInt64BigEndian(tt.AsSpan(pos)); pos += 8; break;
                case 0x8: v = BinaryPrimitives.ReadSingleBigEndian(tt.AsSpan(pos)); pos += 4; break;
                case 0xA: v = Str(BinaryPrimitives.ReadInt32BigEndian(tt.AsSpan(pos))); pos += 4; break;
                case 0xB:
                    {
                        int o = BinaryPrimitives.ReadInt32BigEndian(tt.AsSpan(pos)), l = BinaryPrimitives.ReadInt32BigEndian(tt.AsSpan(pos + 4));
                        v = (dataOff + o >= 0 && dataOff + o + l <= tt.Length) ? tt.AsSpan(dataOff + o, l).ToArray() : Array.Empty<byte>();
                        pos += 8;
                        break;
                    }
                default: throw new InvalidDataException($"unknown @UTF column type 0x{type:X}");
            }
            return v;
        }

        var cols = new List<(string name, int storage, int type, object? constant)>();
        int p = 24;
        for (int i = 0; i < colCount; i++)
        {
            int flags = tt[p];
            string name = Str(BinaryPrimitives.ReadInt32BigEndian(tt.AsSpan(p + 1)));
            p += 5;
            int storage = flags & 0xF0, type = flags & 0x0F;
            object? constant = null;
            if (storage == 0x30) constant = Value(type, ref p);
            cols.Add((name, storage, type, constant));
        }
        var rows = new List<Dictionary<string, object?>>(rowCount);
        for (int r = 0; r < rowCount; r++)
        {
            int pos = rowsOff + r * rowLen;
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, storage, type, constant) in cols)
                row[name] = storage switch
                {
                    0x50 => Value(type, ref pos),
                    0x30 => constant,
                    _ => null,
                };
            rows.Add(row);
        }
        return new UtfTable(Str(nameOff), cols.ConvertAll(c => c.name), rows);
    }

    /// <summary>Write a table (tests and tools). Every column is per-row; types: byte/ushort/uint/ulong/string/byte[].</summary>
    public static byte[] Write(string name, IReadOnlyList<string> columns, IReadOnlyList<object[]> rows)
    {
        var strings = new MemoryStream();
        var strIndex = new Dictionary<string, int>();
        int S(string s)
        {
            if (strIndex.TryGetValue(s, out var o)) return o;
            o = (int)strings.Length;
            var b = Encoding.UTF8.GetBytes(s);
            strings.Write(b); strings.WriteByte(0);
            strIndex[s] = o;
            return o;
        }
        S("<NULL>");
        int tableName = S(name);
        static int TypeOf(object v) => v switch { byte => 0x0, ushort => 0x2, uint => 0x4, ulong => 0x6, string => 0xA, byte[] => 0xB, _ => throw new ArgumentException(v.GetType().Name) };
        static int SizeOf(int type) => type switch { 0x0 => 1, 0x2 => 2, 0x4 => 4, 0x6 => 8, 0xA => 4, 0xB => 8, _ => 0 };
        var types = new int[columns.Count];
        for (int c = 0; c < columns.Count; c++) types[c] = TypeOf(rows[0][c]);
        int rowLen = 0;
        foreach (var ty in types) rowLen += SizeOf(ty);

        var colBytes = new MemoryStream();
        for (int c = 0; c < columns.Count; c++)
        {
            colBytes.WriteByte((byte)(0x50 | types[c]));
            colBytes.Write(BE(S(columns[c])));
        }
        var data = new MemoryStream();
        var rowBytes = new MemoryStream();
        foreach (var row in rows)
            for (int c = 0; c < columns.Count; c++)
                switch (row[c])
                {
                    case byte b: rowBytes.WriteByte(b); break;
                    case ushort u: rowBytes.Write(new[] { (byte)(u >> 8), (byte)u }); break;
                    case uint u: rowBytes.Write(BE((int)u)); break;
                    case ulong u: rowBytes.Write(BE((int)(u >> 32))); rowBytes.Write(BE((int)u)); break;
                    case string s: rowBytes.Write(BE(S(s))); break;
                    case byte[] blob: rowBytes.Write(BE((int)data.Length)); rowBytes.Write(BE(blob.Length)); data.Write(blob); break;
                }
        int rowsOff = 24 + (int)colBytes.Length;
        int strOff = rowsOff + (int)rowBytes.Length;
        int dataOff = strOff + (int)strings.Length;
        var t = new MemoryStream();
        t.Write(new byte[] { 0, 1 });
        t.Write(new[] { (byte)(rowsOff >> 8), (byte)rowsOff });
        t.Write(BE(strOff)); t.Write(BE(dataOff)); t.Write(BE(tableName));
        t.Write(new[] { (byte)(columns.Count >> 8), (byte)columns.Count, (byte)(rowLen >> 8), (byte)rowLen });
        t.Write(BE(rows.Count));
        colBytes.WriteTo(t); rowBytes.WriteTo(t); strings.WriteTo(t); data.WriteTo(t);
        var o2 = new MemoryStream();
        o2.Write("@UTF"u8); o2.Write(BE((int)t.Length)); t.WriteTo(o2);
        return o2.ToArray();
    }

    static byte[] BE(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}

/// <summary>
/// CRI CPK archive (used by Catherine Classic's data/sound/*.cpk, and by Persona 4 Golden's 64-bit release).
/// "CPK " chunk with a CpkHeader @UTF table; its TocOffset points at a "TOC " chunk whose @UTF rows are
/// DirName, FileName, FileSize (stored), ExtractSize, FileOffset (relative to the TOC chunk).
/// Archives without a TOC (ID-only, "ITOC") are listed by ID.
/// </summary>
public sealed class CriCpk : IDisposable
{
    readonly Stream _s;
    public IReadOnlyList<CpkEntry> Entries { get; }
    public UtfTable Header { get; }

    public CriCpk(Stream s)
    {
        _s = s;
        var head = new byte[16];
        s.Position = 0;
        s.ReadExactly(head);
        if (!head.AsSpan(0, 4).SequenceEqual("CPK "u8)) throw new InvalidDataException("not a CRI CPK");
        long hsize = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8));
        var hbytes = new byte[hsize];
        s.ReadExactly(hbytes);
        Header = UtfTable.Read(hbytes);
        var h = Header.Rows[0];
        ulong tocOff = AsU64(h.GetValueOrDefault("TocOffset"));
        ulong contentOff = AsU64(h.GetValueOrDefault("ContentOffset"));
        ulong itocOff = AsU64(h.GetValueOrDefault("ItocOffset"));
        var list = new List<CpkEntry>();
        if (tocOff != 0)
        {
            var toc = ReadChunk((long)tocOff, "TOC ");
            long baseOff = contentOff != 0 ? Math.Min((long)tocOff, (long)contentOff) : (long)tocOff;
            foreach (var r in toc.Rows)
            {
                string dir = r.GetValueOrDefault("DirName") as string ?? "", file = r.GetValueOrDefault("FileName") as string ?? "";
                string path = string.IsNullOrEmpty(dir) ? file : dir.TrimEnd('/') + "/" + file;
                list.Add(new CpkEntry(path, baseOff + (long)AsU64(r.GetValueOrDefault("FileOffset")),
                    (long)AsU64(r.GetValueOrDefault("FileSize")), (long)AsU64(r.GetValueOrDefault("ExtractSize"))));
            }
        }
        else if (itocOff != 0)
        {
            var itoc = ReadChunk((long)itocOff, "ITOC");
            foreach (var r in itoc.Rows)
                foreach (var blobCol in new[] { "DataL", "DataH" })
                    if (r.GetValueOrDefault(blobCol) is byte[] blob && blob.Length > 0)
                        foreach (var e in UtfTable.Read(blob).Rows)
                            list.Add(new CpkEntry($"id_{AsU64(e.GetValueOrDefault("ID")):D5}", 0,
                                (long)AsU64(e.GetValueOrDefault("FileSize")), (long)AsU64(e.GetValueOrDefault("ExtractSize"))));
        }
        Entries = list;
    }

    public static CriCpk Open(string path) => new(File.OpenRead(path));

    UtfTable ReadChunk(long offset, string magic)
    {
        var head = new byte[16];
        _s.Position = offset;
        _s.ReadExactly(head);
        if (!head.AsSpan(0, 4).SequenceEqual(Encoding.ASCII.GetBytes(magic))) throw new InvalidDataException($"expected {magic} chunk at 0x{offset:X}");
        var b = new byte[BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8))];
        _s.ReadExactly(b);
        return UtfTable.Read(b);
    }

    static ulong AsU64(object? v) => v switch
    {
        null => 0, byte b => b, ushort u => u, uint u => u, ulong u => u, short s => (ulong)s, int i => (ulong)i, long l => (ulong)l, _ => 0,
    };

    /// <summary>Stored bytes of an entry (still CRILAYLA-compressed if <see cref="CpkEntry.Compressed"/>).</summary>
    public byte[] ReadStored(CpkEntry e)
    {
        var b = new byte[e.StoredSize];
        lock (_s) { _s.Position = e.Offset; _s.ReadExactly(b); }
        return b;
    }

    /// <summary>An entry's bytes, decompressed if it is stored CRILAYLA-compressed.</summary>
    public byte[] Read(CpkEntry e)
    {
        var stored = ReadStored(e);
        return Crilayla.IsCompressed(stored) ? Crilayla.Decompress(stored) : stored;
    }

    /// <summary>Read only the first bytes of an entry (headers of big audio archives).</summary>
    public byte[] ReadHead(CpkEntry e, int count)
    {
        var b = new byte[Math.Min(count, e.StoredSize)];
        lock (_s) { _s.Position = e.Offset; _s.ReadExactly(b); }
        return b;
    }

    public byte[] ReadRange(long absoluteOffset, int count)
    {
        var b = new byte[count];
        lock (_s) { _s.Position = absoluteOffset; _s.ReadExactly(b); }
        return b;
    }

    public void Dispose() => _s.Dispose();

    /// <summary>Build a minimal TOC-based CPK (tests).</summary>
    public static byte[] Build(IReadOnlyList<(string path, byte[] data)> files)
    {
        const long tocOffset = 0x800;
        var tocRows = new List<object[]>();
        long contentRel = 0x800; // content right after a 0x800-aligned TOC area
        var placed = new List<(long rel, byte[] data)>();
        foreach (var (path, data) in files)
        {
            int slash = path.LastIndexOf('/');
            tocRows.Add(new object[] { slash < 0 ? "" : path[..slash], slash < 0 ? path : path[(slash + 1)..], (uint)data.Length, (uint)data.Length, (ulong)contentRel });
            placed.Add((contentRel, data));
            contentRel += (data.Length + 0x7FF) & ~0x7FF;
        }
        var toc = UtfTable.Write("CpkTocInfo", new[] { "DirName", "FileName", "FileSize", "ExtractSize", "FileOffset" }, tocRows);
        var header = UtfTable.Write("CpkHeader", new[] { "ContentOffset", "TocOffset", "ItocOffset" }, new[] { new object[] { (ulong)(tocOffset + 0x800), (ulong)tocOffset, 0UL } });
        var ms = new MemoryStream();
        void Chunk(string magic, byte[] body)
        {
            ms.Write(Encoding.ASCII.GetBytes(magic)); ms.Write(BitConverter.GetBytes(0xFF));
            ms.Write(BitConverter.GetBytes((long)body.Length)); ms.Write(body);
        }
        Chunk("CPK ", header);
        ms.SetLength(tocOffset); ms.Position = tocOffset;
        Chunk("TOC ", toc);
        foreach (var (rel, data) in placed) { ms.SetLength(Math.Max(ms.Length, tocOffset + rel)); ms.Position = tocOffset + rel; ms.Write(data); }
        return ms.ToArray();
    }
}
