using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace InvestigationNightmares.Formats;

/// <summary>
/// Chunked Huffman compression used by entries of Persona 4 Golden PC's DW_PACK archives.
/// Layout: header {int magic=0x1234, chunkCount, chunkSize, headerSize}, then chunkCount x
/// {int uncompressedSize, compressedSize, dataOffset}; chunk data starts at headerSize + dataOffset.
/// Each chunk is an MSB-first bit stream: the tree in preorder (1 = internal node, 0 + 8 bits = leaf),
/// then one code per output byte (0 = left, 1 = right).
/// </summary>
public static class PreappHuffman
{
    public const int Magic = 0x1234;
    public const int DefaultChunkSize = 0x20000;

    sealed class Node
    {
        public Node? Left, Right;
        public byte Value;
        public bool IsLeaf;
    }

    ref struct BitReader
    {
        readonly ReadOnlySpan<byte> _src;
        int _pos;
        int _bit;

        public BitReader(ReadOnlySpan<byte> src) { _src = src; _pos = 0; _bit = 0; }

        public bool Read()
        {
            if (_pos >= _src.Length) throw new InvalidDataException("Huffman chunk ended early");
            bool v = (_src[_pos] & (0x80 >> _bit)) != 0;
            if (++_bit == 8) { _bit = 0; _pos++; }
            return v;
        }
    }

    public static bool LooksCompressed(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && BinaryPrimitives.ReadInt32LittleEndian(data) == Magic;

    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (!LooksCompressed(src)) throw new InvalidDataException("missing 0x1234 Huffman header");
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(src[4..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(src[12..]);
        var dst = new byte[uncompressedSize];
        int dstOff = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var ch = src.Slice(16 + i * 12, 12);
            int usize = BinaryPrimitives.ReadInt32LittleEndian(ch);
            int csize = BinaryPrimitives.ReadInt32LittleEndian(ch[4..]);
            int off = BinaryPrimitives.ReadInt32LittleEndian(ch[8..]);
            if (dstOff + usize > dst.Length) throw new InvalidDataException("Huffman output larger than declared");
            DecompressChunk(src.Slice(headerSize + off, csize), dst.AsSpan(dstOff, usize));
            dstOff += usize;
        }
        if (dstOff != uncompressedSize) throw new InvalidDataException($"Huffman output {dstOff} != {uncompressedSize}");
        return dst;
    }

    static void DecompressChunk(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var br = new BitReader(src);
        var root = ReadTree(ref br, 0);
        for (int i = 0; i < dst.Length; i++)
        {
            var n = root;
            while (!n.IsLeaf) n = br.Read() ? n.Right! : n.Left!;
            dst[i] = n.Value;
        }
    }

    static Node ReadTree(ref BitReader br, int depth)
    {
        if (depth > 256) throw new InvalidDataException("Huffman tree too deep");
        var n = new Node();
        if (br.Read())
        {
            n.Left = ReadTree(ref br, depth + 1);
            n.Right = ReadTree(ref br, depth + 1);
        }
        else
        {
            byte v = 0;
            for (int i = 0; i < 8; i++) if (br.Read()) v |= (byte)(0x80 >> i);
            n.Value = v;
            n.IsLeaf = true;
        }
        return n;
    }

    /// <summary>Compress into the same layout (used for test fixtures and repacking).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> src, int chunkSize = DefaultChunkSize)
    {
        int chunkCount = Math.Max(1, (src.Length + chunkSize - 1) / chunkSize);
        int headerSize = 16 + chunkCount * 12;
        var chunks = new List<byte[]>();
        for (int i = 0; i < chunkCount; i++)
        {
            int start = i * chunkSize;
            chunks.Add(CompressChunk(src.Slice(start, Math.Min(chunkSize, src.Length - start))));
        }
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(Magic); bw.Write(chunkCount); bw.Write(chunkSize); bw.Write(headerSize);
        int dataOff = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            int start = i * chunkSize;
            bw.Write(Math.Min(chunkSize, src.Length - start));
            bw.Write(chunks[i].Length);
            bw.Write(dataOff);
            dataOff += chunks[i].Length;
        }
        foreach (var c in chunks) bw.Write(c);
        return ms.ToArray();
    }

    static byte[] CompressChunk(ReadOnlySpan<byte> src)
    {
        var counts = new long[256];
        foreach (var b in src) counts[b]++;
        var pq = new PriorityQueue<Node, (long, int)>();
        int order = 0;
        for (int v = 0; v < 256; v++)
            if (counts[v] > 0) pq.Enqueue(new Node { IsLeaf = true, Value = (byte)v }, (counts[v], order++));
        if (pq.Count == 0) pq.Enqueue(new Node { IsLeaf = true }, (0, order++));
        while (pq.Count > 1)
        {
            pq.TryDequeue(out var a, out var pa);
            pq.TryDequeue(out var b, out var pb);
            pq.Enqueue(new Node { Left = a, Right = b }, (pa.Item1 + pb.Item1, order++));
        }
        var root = pq.Dequeue();
        var codes = new (ulong bits, int len)[256];
        Assign(root, 0, 0, codes);

        var bits = new List<bool>(src.Length * 4);
        WriteTree(root, bits);
        foreach (var b in src)
        {
            var (code, len) = codes[b];
            if (len > 64) throw new InvalidOperationException("Huffman code too long");
            for (int i = len - 1; i >= 0; i--) bits.Add(((code >> i) & 1) != 0);
        }
        var outBytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) outBytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return outBytes;
    }

    static void Assign(Node n, ulong code, int len, (ulong, int)[] codes)
    {
        if (n.IsLeaf) { codes[n.Value] = (code, len); return; }
        Assign(n.Left!, code << 1, len + 1, codes);
        Assign(n.Right!, (code << 1) | 1, len + 1, codes);
    }

    static void WriteTree(Node n, List<bool> bits)
    {
        if (!n.IsLeaf) { bits.Add(true); WriteTree(n.Left!, bits); WriteTree(n.Right!, bits); return; }
        bits.Add(false);
        for (int i = 0; i < 8; i++) bits.Add((n.Value & (0x80 >> i)) != 0);
    }
}
