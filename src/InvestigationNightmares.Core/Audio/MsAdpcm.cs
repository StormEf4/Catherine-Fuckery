using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace InvestigationNightmares.Audio;

/// <summary>Microsoft ADPCM (WAVE_FORMAT_ADPCM) block decoder with the standard 7 coefficient pairs.</summary>
public static class MsAdpcm
{
    static readonly int[] Coef1 = { 256, 512, 0, 192, 240, 460, 392 };
    static readonly int[] Coef2 = { 0, -256, 0, 64, 0, -208, -232 };
    static readonly int[] Adapt = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };

    public static short[] Decode(ReadOnlySpan<byte> data, int channels, int blockAlign)
    {
        if (channels < 1 || channels > 2) throw new NotSupportedException($"{channels}-channel ADPCM");
        int samplesPerBlock = (blockAlign - 7 * channels) * 2 / channels + 2;
        var output = new List<short>(data.Length / blockAlign * samplesPerBlock * channels + 64);
        Span<int> c1 = stackalloc int[2], c2 = stackalloc int[2], delta = stackalloc int[2], s1 = stackalloc int[2], s2 = stackalloc int[2];

        for (int blockStart = 0; blockStart + 7 * channels <= data.Length; blockStart += blockAlign)
        {
            var b = data.Slice(blockStart, Math.Min(blockAlign, data.Length - blockStart));
            int p = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int pred = Math.Min((int)b[p++], 6);
                c1[ch] = Coef1[pred]; c2[ch] = Coef2[pred];
            }
            for (int ch = 0; ch < channels; ch++) { delta[ch] = BinaryPrimitives.ReadInt16LittleEndian(b[p..]); p += 2; }
            for (int ch = 0; ch < channels; ch++) { s1[ch] = BinaryPrimitives.ReadInt16LittleEndian(b[p..]); p += 2; }
            for (int ch = 0; ch < channels; ch++) { s2[ch] = BinaryPrimitives.ReadInt16LittleEndian(b[p..]); p += 2; }
            for (int ch = 0; ch < channels; ch++) output.Add((short)s2[ch]);
            for (int ch = 0; ch < channels; ch++) output.Add((short)s1[ch]);

            int ch2 = 0;
            for (; p < b.Length; p++)
            {
                foreach (int nib in new[] { b[p] >> 4, b[p] & 0xF })
                {
                    int signedNib = nib >= 8 ? nib - 16 : nib;
                    int predSample = (s1[ch2] * c1[ch2] + s2[ch2] * c2[ch2]) >> 8;
                    predSample += signedNib * delta[ch2];
                    predSample = Math.Clamp(predSample, short.MinValue, short.MaxValue);
                    output.Add((short)predSample);
                    s2[ch2] = s1[ch2];
                    s1[ch2] = predSample;
                    delta[ch2] = Math.Max(16, Adapt[nib] * delta[ch2] >> 8);
                    ch2 = (ch2 + 1) % channels;
                }
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// Simple encoder (predictor 0 for every block, greedy nibbles). Good enough to build test fixtures
    /// and to round-trip clips; not tuned for quality.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels, int blockAlign)
    {
        int spb = (blockAlign - 7 * channels) * 2 / channels + 2;
        var src = interleaved.ToArray();
        int frames = src.Length / channels;
        var outBytes = new List<byte>();
        Span<int> delta = stackalloc int[2], s1 = stackalloc int[2], s2 = stackalloc int[2];
        for (int f0 = 0; f0 < frames; f0 += spb)
        {
            var block = new byte[blockAlign];
            int p = 0;
            int Get(int frame, int ch) => frame < frames ? src[frame * channels + ch] : 0;
            for (int ch = 0; ch < channels; ch++) block[p++] = 0;
            for (int ch = 0; ch < channels; ch++) { delta[ch] = 16; BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(p), 16); p += 2; }
            for (int ch = 0; ch < channels; ch++) { s1[ch] = Get(f0 + 1, ch); BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(p), (short)s1[ch]); p += 2; }
            for (int ch = 0; ch < channels; ch++) { s2[ch] = Get(f0, ch); BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(p), (short)s2[ch]); p += 2; }
            int nibIndex = 0;
            for (int f = f0 + 2; f < f0 + spb; f++)
                for (int ch = 0; ch < channels; ch++)
                {
                    int predSample = (s1[ch] * 256) >> 8;
                    int err = Get(f, ch) - predSample;
                    int n = Math.Clamp((int)Math.Round(err / (double)delta[ch]), -8, 7);
                    int recon = Math.Clamp(predSample + n * delta[ch], short.MinValue, short.MaxValue);
                    s2[ch] = s1[ch]; s1[ch] = recon;
                    int un = n & 0xF;
                    delta[ch] = Math.Max(16, Adapt[un] * delta[ch] >> 8);
                    int bytePos = p + nibIndex / 2;
                    if ((nibIndex & 1) == 0) block[bytePos] = (byte)(un << 4); else block[bytePos] |= (byte)un;
                    nibIndex++;
                }
            outBytes.AddRange(block);
        }
        return outBytes.ToArray();
    }
}

/// <summary>Interleaved 16-bit PCM.</summary>
public sealed record Pcm16(int Channels, int SampleRate, short[] Samples)
{
    public double DurationSeconds => Samples.Length / (double)(Channels * SampleRate);

    public byte[] ToWav()
    {
        int dataLen = Samples.Length * 2;
        var b = new byte[44 + dataLen];
        var s = b.AsSpan();
        "RIFF"u8.CopyTo(s); BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataLen);
        "WAVE"u8.CopyTo(s[8..]); "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)Channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], SampleRate * Channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(Channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]); BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataLen);
        Buffer.BlockCopy(Samples, 0, b, 44, dataLen);
        return b;
    }

    public static Pcm16 FromWav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new System.IO.InvalidDataException("not a RIFF WAVE file");
        int pos = 12, channels = 0, rate = 0, bits = 0, fmtTag = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(wav[(pos + 4)..]);
            var body = wav.Slice(pos + 8, Math.Min(len, wav.Length - pos - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                fmtTag = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (fmtTag != 1 || bits != 16) throw new NotSupportedException($"WAV format {fmtTag}/{bits}-bit");
                var s = new short[body.Length / 2];
                for (int i = 0; i < s.Length; i++) s[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]);
                return new Pcm16(channels, rate, s);
            }
            pos += 8 + len + (len & 1);
        }
        throw new System.IO.InvalidDataException("WAV without data chunk");
    }
}
