using System;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X64;
using X86 = Reloaded.Hooks.Definitions.X86;

namespace InvestigationNightmares.Mod;

/// <summary>Win32 imports and the signatures of every function the mod hooks (see sheets/hooks.json).</summary>
static unsafe class Native
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint LoadLibraryW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] public static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32")] public static extern int QueryPerformanceFrequency(out long freq);
    [DllImport("kernel32")] public static extern ulong GetTickCount64();
    [DllImport("kernel32")] public static extern uint GetCurrentProcessId();

    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32")] public static extern nint GetDesktopWindow();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32")] public static extern nint CallWindowProcW(nint prev, nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32")] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] public static extern int DestroyWindow(nint hwnd);

    public const int GWLP_WNDPROC = -4;
    public const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_INPUT = 0x00FF;

    [DllImport("user32")] static extern uint SendInput(uint count, byte* inputs, int size);

    /// <summary>Press or release a key by scan code (what DirectInput and raw input see).</summary>
    public static void SendKey(int vk, bool down)
    {
        // INPUT is 28 bytes on x86 (union at 4) and 40 bytes on x64 (union at 8).
        int size = IntPtr.Size == 8 ? 40 : 28, union = IntPtr.Size == 8 ? 8 : 4;
        byte* buf = stackalloc byte[40];
        new Span<byte>(buf, 40).Clear();
        *(uint*)buf = 1; // INPUT_KEYBOARD
        *(ushort*)(buf + union) = 0; // wVk
        *(ushort*)(buf + union + 2) = (ushort)MapVirtualKeyW((uint)vk, 0); // wScan
        *(uint*)(buf + union + 4) = 0x0008u | (down ? 0u : 0x0002u); // KEYEVENTF_SCANCODE | KEYUP
        SendInput(1, buf, size);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

    // ---- hooked signatures: stdcall on x86, Microsoft x64 on x64

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate int PresentFn(nint device, nint sourceRect, nint destRect, nint destWindow, nint dirtyRegion);

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate int ResetFn(nint device, nint presentParams);

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate nint CreateFileFn(nint fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate int QueryPerformanceCounterFn(long* counter);

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate uint TimeGetTimeFn();

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate uint XInputGetStateFn(uint userIndex, XINPUT_STATE* state);

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate short GetAsyncKeyStateFn(int vk);

    [X86.Function(X86.CallingConventions.Stdcall)][Function(CallingConventions.Microsoft)]
    public delegate int GetDeviceStateFn(nint self, uint size, byte* data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate nint WndProcFn(nint hwnd, uint msg, nint w, nint l);

    /// <summary>Address of an exported function, preferring kernelbase for kernel32 APIs (kernel32's exports are jump stubs).</summary>
    public static nint Export(string module, string name)
    {
        if (module.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase))
        {
            var kb = GetModuleHandleW("kernelbase.dll");
            if (kb != 0) { var a = GetProcAddress(kb, name); if (a != 0) return a; }
        }
        var m = GetModuleHandleW(module);
        if (m == 0) m = LoadLibraryW(module);
        return m == 0 ? 0 : GetProcAddress(m, name);
    }
}
