using System;
using System.Collections.Generic;
using InvestigationNightmares.Formats;

namespace InvestigationNightmares.Images;

/// <summary>
/// Pulls every picture out of a P4G file whatever it is wrapped in: a bare TMX or DDS, an Atlus PAK of
/// them (nested any depth), or an unknown container that embeds TMX/DDS data.
/// </summary>
public static class ImageExtractor
{
    public sealed record Found(string Name, Bgra32Image Image);

    public static List<Found> Extract(ReadOnlySpan<byte> data, string name, List<string>? problems = null)
    {
        var found = new List<Found>();
        Walk(data, name, found, problems, 0);
        return found;
    }

    static void Walk(ReadOnlySpan<byte> d, string name, List<Found> found, List<string>? problems, int depth)
    {
        if (depth > 6) return;
        if (Tmx.IsTmx(d)) { var arr = d.ToArray(); TryAdd(() => Tmx.Decode(arr), name, found, problems); return; }
        if (Dds.IsDds(d)) { var arr = d.ToArray(); TryAdd(() => Dds.Decode(arr), name, found, problems); return; }
        if (AtlusPak.TryRead(d, out var files))
        {
            foreach (var (n, data) in files) Walk(data, name + "/" + n, found, problems, depth + 1);
            return;
        }
        // Unknown wrapper: look for embedded pictures.
        int hits = 0;
        for (int i = 0; i + 128 <= d.Length && hits < 64; i += 4)
        {
            if (d[i] == (byte)'D' && Dds.IsDds(d[i..]))
            {
                hits++;
                var slice = d[i..].ToArray();
                TryAdd(() => Dds.Decode(slice), $"{name}@{i:X}", found, problems);
            }
            else if (i >= 8 && d[i] == (byte)'T' && Tmx.IsTmx(d[(i - 8)..]))
            {
                hits++;
                var slice = d[(i - 8)..].ToArray();
                TryAdd(() => Tmx.Decode(slice), $"{name}@{i - 8:X}", found, problems);
            }
        }
        if (hits == 0) problems?.Add($"{name}: no TMX/DDS picture inside ({d.Length} bytes, starts {Convert.ToHexString(d[..Math.Min(16, d.Length)])})");
    }

    static void TryAdd(Func<Bgra32Image> decode, string name, List<Found> found, List<string>? problems)
    {
        try { found.Add(new Found(name, decode())); }
        catch (Exception e) { problems?.Add($"{name}: {e.Message}"); }
    }
}
