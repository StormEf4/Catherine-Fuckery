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
        // Unknown wrapper: look for embedded pictures at any byte offset.
        int hits = 0;
        for (int from = 0; from < d.Length && hits < 64;)
        {
            int tmx = d[from..].IndexOf("TMX0"u8), dds = d[from..].IndexOf("DDS "u8);
            if (tmx < 0 && dds < 0) break;
            bool isTmx = tmx >= 0 && (dds < 0 || tmx < dds);
            int at = from + (isTmx ? tmx - 8 : dds);
            from += (isTmx ? tmx : dds) + 4;
            if (at < 0) continue;
            var slice = d[at..].ToArray();
            if (isTmx ? !Tmx.IsTmx(slice) : !Dds.IsDds(slice)) continue;
            hits++;
            TryAdd(isTmx ? () => Tmx.Decode(slice) : () => Dds.Decode(slice), $"{name}@{at:X}", found, problems);
        }
        if (hits == 0) problems?.Add($"{name}: no TMX/DDS picture inside ({d.Length} bytes, starts {Convert.ToHexString(d[..Math.Min(16, d.Length)])})");
    }

    static void TryAdd(Func<Bgra32Image> decode, string name, List<Found> found, List<string>? problems)
    {
        try { found.Add(new Found(name, decode())); }
        catch (Exception e) { problems?.Add($"{name}: {e.Message}"); }
    }
}
