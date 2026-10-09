using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using InvestigationNightmares.Audio;

namespace InvestigationNightmares.Mod;

/// <summary>
/// Plays P4G music and voice barks through winmm's waveOut on its own thread, so audio work never stalls
/// Catherine's render thread. One looping music voice and one bark voice.
/// </summary>
sealed unsafe class AudioPlayer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public nint lpData; public uint dwBufferLength, dwBytesRecorded; public nint dwUser; public uint dwFlags, dwLoops; public nint lpNext, reserved; }

    [DllImport("winmm")] static extern int waveOutOpen(out nint h, uint device, WAVEFORMATEX* fmt, nint cb, nint inst, uint flags);
    [DllImport("winmm")] static extern int waveOutPrepareHeader(nint h, WAVEHDR* hdr, uint size);
    [DllImport("winmm")] static extern int waveOutUnprepareHeader(nint h, WAVEHDR* hdr, uint size);
    [DllImport("winmm")] static extern int waveOutWrite(nint h, WAVEHDR* hdr, uint size);
    [DllImport("winmm")] static extern int waveOutReset(nint h);
    [DllImport("winmm")] static extern int waveOutClose(nint h);

    const uint WHDR_BEGINLOOP = 4, WHDR_ENDLOOP = 8, WAVE_MAPPER = 0xFFFFFFFF;

    sealed class Voice
    {
        public nint Handle;
        public WAVEHDR* Header;
        public nint Data;

        public void Stop()
        {
            if (Handle == 0) return;
            waveOutReset(Handle);
            waveOutUnprepareHeader(Handle, Header, (uint)sizeof(WAVEHDR));
            waveOutClose(Handle);
            Marshal.FreeHGlobal(Data);
            Marshal.FreeHGlobal((nint)Header);
            Handle = 0;
        }
    }

    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;
    readonly Voice _music = new(), _bark = new();
    readonly ModLog _log;

    public AudioPlayer(ModLog log)
    {
        _log = log;
        _thread = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) { try { a(); } catch (Exception e) { _log.Warn($"audio: {e.Message}"); } } })
        { IsBackground = true, Name = "InvestigationNightmares audio" };
        _thread.Start();
    }

    public void PlayMusic(Pcm16 pcm, double volume, bool loop) => _work.Add(() => Start(_music, pcm, volume, loop));
    public void StopMusic() => _work.Add(_music.Stop);
    public void PlayBark(Pcm16 pcm, double volume) => _work.Add(() => Start(_bark, pcm, volume, false));

    void Start(Voice v, Pcm16 pcm, double volume, bool loop)
    {
        v.Stop();
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = (ushort)pcm.Channels, nSamplesPerSec = (uint)pcm.SampleRate,
            wBitsPerSample = 16, nBlockAlign = (ushort)(pcm.Channels * 2), nAvgBytesPerSec = (uint)(pcm.SampleRate * pcm.Channels * 2),
        };
        int err = waveOutOpen(out v.Handle, WAVE_MAPPER, &fmt, 0, 0, 0);
        if (err != 0) { v.Handle = 0; _log.Warn($"waveOutOpen failed ({err})"); return; }
        int bytes = pcm.Samples.Length * 2;
        v.Data = Marshal.AllocHGlobal(bytes);
        var dst = new Span<short>((void*)v.Data, pcm.Samples.Length);
        double vol = Math.Clamp(volume, 0, 2);
        for (int i = 0; i < dst.Length; i++) dst[i] = (short)Math.Clamp(pcm.Samples[i] * vol, short.MinValue, short.MaxValue);
        v.Header = (WAVEHDR*)Marshal.AllocHGlobal(sizeof(WAVEHDR));
        *v.Header = new WAVEHDR { lpData = v.Data, dwBufferLength = (uint)bytes, dwFlags = loop ? WHDR_BEGINLOOP | WHDR_ENDLOOP : 0, dwLoops = loop ? 0x7FFFFFFFu : 0u };
        waveOutPrepareHeader(v.Handle, v.Header, (uint)sizeof(WAVEHDR));
        waveOutWrite(v.Handle, v.Header, (uint)sizeof(WAVEHDR));
    }

    public void Dispose()
    {
        _work.Add(() => { _music.Stop(); _bark.Stop(); });
        _work.CompleteAdding();
    }
}
