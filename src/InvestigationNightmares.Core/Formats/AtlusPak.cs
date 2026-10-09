using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvestigationNightmares.Formats;

/// <summary>
/// Atlus "PAK" v1 container used across Persona 3/4 (.bin/.pak/.pac files inside the game archives):
/// repeated { char name[252]; int32 size; byte data[size]; pad to 64 } until an empty name.
/// </summary>
public static class AtlusPak
{
    const int NameLength = 252;
    const int EntryHeader = 256;

    public static bool TryRead(ReadOnlySpan<byte> d, out List<(string name, byte[] data)> files)
    {
        files = new();
        int pos = 0;
        while (pos + EntryHeader <= d.Length)
        {
            var nameBytes = d.Slice(pos, NameLength);
            int nul = nameBytes.IndexOf((byte)0);
            if (nul == 0) break;
            if (nul < 0) return false;
            foreach (var c in nameBytes[..nul])
                if (c < 0x20 || c > 0x7E) return false;
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[(pos + NameLength)..]);
            if (size < 0 || pos + EntryHeader + size > d.Length) return false;
            files.Add((Encoding.ASCII.GetString(nameBytes[..nul]), d.Slice(pos + EntryHeader, size).ToArray()));
            pos += EntryHeader + size;
            pos = (pos + 63) & ~63;
        }
        return files.Count > 0;
    }

    public static byte[] Build(IEnumerable<(string name, byte[] data)> files)
    {
        using var ms = new MemoryStream();
        Span<byte> sz = stackalloc byte[4];
        foreach (var (name, data) in files)
        {
            var nb = new byte[NameLength];
            Encoding.ASCII.GetBytes(name).CopyTo(nb, 0);
            ms.Write(nb);
            BinaryPrimitives.WriteInt32LittleEndian(sz, data.Length);
            ms.Write(sz);
            ms.Write(data);
            while (ms.Length % 64 != 0) ms.WriteByte(0);
        }
        ms.Write(new byte[EntryHeader]);
        return ms.ToArray();
    }
}
