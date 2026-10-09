using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using InvestigationNightmares.Input;
using InvestigationNightmares.Powers;
using InvestigationNightmares.Story;
using Reloaded.Hooks.Definitions;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using static InvestigationNightmares.Mod.Native;

namespace InvestigationNightmares.Mod;

/// <summary>
/// Every hook from sheets/hooks.json. Only public Windows/DirectX entry points are hooked, nothing inside
/// Catherine.exe. Hook bodies run on whatever game thread calls them, so they only touch thread-safe state
/// and hand events to the render thread through queues.
/// </summary>
sealed unsafe class GameHooks
{
    readonly IReloadedHooks _hooks;
    readonly ModLog _log;
    readonly string _gameRoot;
    readonly bool _logFileOpens;

    // Keep delegates and hooks alive for the life of the process.
    readonly List<object> _keepAlive = new();

    // ---- triggers / music redirect
    public readonly ConcurrentQueue<string> FileOpens = new();
    readonly ConcurrentDictionary<string, byte> _seenFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, nint> _redirects = new(StringComparer.OrdinalIgnoreCase);
    IHook<CreateFileFn>? _createFileW, _createFileA;

    // ---- time
    readonly TimeScaler _qpcScale = new(), _msScale = new();
    IHook<QueryPerformanceCounterFn>? _qpc;
    IHook<TimeGetTimeFn>? _timeGetTime;

    // ---- input
    public readonly ConcurrentQueue<string> Hotkeys = new();
    public volatile bool InputBlocked;
    public volatile bool PadSeen;
    IHook<XInputGetStateFn>? _xinput;
    IHook<GetAsyncKeyStateFn>? _getAsyncKeyState;
    IHook<GetDeviceStateFn>? _dinputGetState;
    readonly ComboTracker _combos;
    readonly (string id, int vk)[] _keys;
    readonly HashSet<int> _keysDown = new();
    nint _wndProcPrev, _hookedWindow;
    WndProcFn? _wndProc;

    // ---- Garu's pull macro (set on the render thread, read by the XInput hook)
    readonly object _macroGate = new();
    AutoPullMacro? _macro;
    double _macroStart, _macroScale;
    bool _sentGrab, _sentBack;
    public readonly ConcurrentQueue<bool> MacroFinished = new();
    readonly ushort _grabMask, _backMask;
    readonly int _grabVk, _backVk;

    // ---- D3D9
    IHook<PresentFn>? _present;
    IHook<ResetFn>? _reset;
    public Action<nint>? OnFrame;
    public Action? OnDeviceReset;

    public GameHooks(IReloadedHooks hooks, ModLog log, string gameRoot, bool logFileOpens)
    {
        _hooks = hooks;
        _log = log;
        _gameRoot = gameRoot.Replace('\\', '/').TrimEnd('/');
        _logFileOpens = logFileOpens;
        var mod = Generated.Sheets.Bindings.Where(b => b.Owner == "mod").ToList();
        _combos = new ComboTracker(mod.Select(b => (b.Id, InputNames.ButtonMask(b.XinputButtons))), ComboTracker.ModifierOf(mod.Select(b => b.XinputButtons)));
        _keys = mod.Select(b => (b.Id, InputNames.VirtualKey(b.Vk))).ToArray();
        _grabMask = InputNames.ButtonMask(SheetIndex.Bindings["cc_grab"].XinputButtons);
        _backMask = InputNames.ButtonMask(SheetIndex.Bindings["cc_back"].XinputButtons);
        _grabVk = InputNames.VirtualKey(SheetIndex.Bindings["cc_grab"].Vk);
        _backVk = InputNames.VirtualKey(SheetIndex.Bindings["cc_back"].Vk);
    }

    public static double RealSeconds => GetTickCount64() / 1000.0;

    IHook<T>? Hook<T>(string what, nint address, T body) where T : Delegate
    {
        if (address == 0) { _log.Warn($"hook {what}: function not found"); return null; }
        try
        {
            var h = _hooks.CreateHook(body, (long)address).Activate();
            _keepAlive.Add(body);
            _keepAlive.Add(h);
            _log.Info($"hooked {what} at 0x{address:X}");
            return h;
        }
        catch (Exception e) { _log.Error($"hook {what} failed: {e.Message}"); return null; }
    }

    public void InstallEarly()
    {
        _createFileW = Hook<CreateFileFn>("CreateFileW", Export("kernel32.dll", "CreateFileW"), CreateFileWImpl);
        _createFileA = Hook<CreateFileFn>("CreateFileA", Export("kernel32.dll", "CreateFileA"), CreateFileAImpl);
        _qpc = Hook<QueryPerformanceCounterFn>("QueryPerformanceCounter", Export("kernel32.dll", "QueryPerformanceCounter"), QpcImpl);
        _timeGetTime = Hook<TimeGetTimeFn>("timeGetTime", Export("winmm.dll", "timeGetTime"), TimeGetTimeImpl);
        _getAsyncKeyState = Hook<GetAsyncKeyStateFn>("GetAsyncKeyState", Export("user32.dll", "GetAsyncKeyState"), GetAsyncKeyStateImpl);
        TryHookXInput();
        TryHookDirectInput();
        try
        {
            var (present, reset) = Overlay.D3D9.FindDeviceFunctions();
            _present = Hook<PresentFn>("IDirect3DDevice9::Present", present, PresentImpl);
            _reset = Hook<ResetFn>("IDirect3DDevice9::Reset", reset, ResetImpl);
        }
        catch (Exception e) { _log.Error($"Direct3D 9 not hookable: {e.Message}. The overlay won't show."); }
    }

    // ------------------------------------------------------------------ files

    nint CreateFileWImpl(nint name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl)
    {
        if (name != 0)
        {
            var path = Marshal.PtrToStringUni(name);
            if (path != null)
            {
                Seen(path);
                if (_redirects.TryGetValue(Normalize(path), out var target)) name = target;
            }
        }
        return _createFileW!.OriginalFunction(name, access, share, sec, disp, flags, tmpl);
    }

    nint CreateFileAImpl(nint name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl)
    {
        if (name != 0)
        {
            var path = Marshal.PtrToStringAnsi(name);
            if (path != null)
            {
                Seen(path);
                if (_redirects.TryGetValue(Normalize(path), out var target) && _createFileW != null)
                    return _createFileW.OriginalFunction(target, access, share, sec, disp, flags, tmpl);
            }
        }
        return _createFileA!.OriginalFunction(name, access, share, sec, disp, flags, tmpl);
    }

    void Seen(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.IndexOf("/data/", StringComparison.OrdinalIgnoreCase) < 0 && !p.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return;
        FileOpens.Enqueue(p);
        if (_logFileOpens && _seenFiles.TryAdd(p, 0)) FileOpenLog.Enqueue($"{RealSeconds:F2}s open {p}");
    }

    public readonly ConcurrentQueue<string> FileOpenLog = new();

    string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (!Path.IsPathRooted(p)) p = _gameRoot + "/" + p.TrimStart('.', '/');
        return p.Replace("//", "/");
    }

    /// <summary>Make Catherine open <paramref name="replacement"/> whenever it opens <paramref name="gameRelative"/>.</summary>
    public void Redirect(string gameRelative, string replacement)
    {
        var key = Normalize(Path.Combine(_gameRoot, gameRelative));
        _redirects[key] = Marshal.StringToHGlobalUni(replacement);
        _log.Info($"redirect {key} -> {replacement}");
    }

    // ------------------------------------------------------------------ time

    int QpcImpl(long* counter)
    {
        int ok = _qpc!.OriginalFunction(counter);
        if (ok != 0 && counter != null) *counter = _qpcScale.ToGame(*counter);
        return ok;
    }

    uint TimeGetTimeImpl() => (uint)_msScale.ToGame(_timeGetTime!.OriginalFunction());

    public void SetTimeScale(double scale)
    {
        if (_qpc != null)
        {
            long real;
            _qpc.OriginalFunction(&real);
            _qpcScale.SetScale(scale, real);
        }
        if (_timeGetTime != null) _msScale.SetScale(scale, _timeGetTime.OriginalFunction());
        _log.Info($"game clock x{scale}");
    }

    // ------------------------------------------------------------------ input

    public void TryHookXInput()
    {
        if (_xinput != null) return;
        foreach (var dll in Generated.Sheets.Hooks.First(h => h.Id == "xinput_getstate").Module.Split('|'))
        {
            if (GetModuleHandleW(dll) == 0) continue;
            _xinput = Hook<XInputGetStateFn>($"{dll}!XInputGetState", Export(dll, "XInputGetState"), XInputGetStateImpl);
            if (_xinput != null) return;
        }
    }

    uint XInputGetStateImpl(uint index, XINPUT_STATE* state)
    {
        uint result = _xinput!.OriginalFunction(index, state);
        if (index != 0 || state == null) return result;
        if (result == 0)
        {
            PadSeen = true;
            var (fired, forGame) = _combos.Poll(state->wButtons);
            foreach (var f in fired) Hotkeys.Enqueue(f);
            state->wButtons = forGame;
        }
        if (InputBlocked && result == 0)
        {
            state->wButtons = 0;
            state->bLeftTrigger = state->bRightTrigger = 0;
            state->sThumbLX = state->sThumbLY = state->sThumbRX = state->sThumbRY = 0;
        }
        if (result == 0)
        {
            var (grab, back) = MacroButtons();
            if (grab) state->wButtons |= _grabMask;
            if (back) state->wButtons |= _backMask;
            if (grab || back) state->sThumbLX = state->sThumbLY = 0;
        }
        return result;
    }

    short GetAsyncKeyStateImpl(int vk) => InputBlocked ? (short)0 : _getAsyncKeyState!.OriginalFunction(vk);

    bool KeyDownReal(int vk) => (_getAsyncKeyState != null ? _getAsyncKeyState.OriginalFunction(vk) : (short)0) < 0;

    void TryHookDirectInput()
    {
        if (_dinputGetState != null || GetModuleHandleW("dinput8.dll") == 0) return;
        try
        {
            var create = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, nint, int>)Export("dinput8.dll", "DirectInput8Create");
            var iid = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
            var keyboard = new Guid("6F1D2B61-D5A0-11CF-BFC7-444553540000");
            nint di, dev;
            if (create(GetModuleHandleW(null), 0x0800, &iid, &di, 0) < 0) return;
            var createDevice = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, nint, int>)(*(nint**)di)[3];
            if (createDevice(di, &keyboard, &dev, 0) >= 0)
            {
                _dinputGetState = Hook<GetDeviceStateFn>("IDirectInputDevice8::GetDeviceState", (*(nint**)dev)[9], GetDeviceStateImpl);
                Overlay.D3D9.Release(dev);
            }
            Overlay.D3D9.Release(di);
        }
        catch (Exception e) { _log.Warn($"DirectInput hook: {e.Message}"); }
    }

    int GetDeviceStateImpl(nint self, uint size, byte* data)
    {
        int hr = _dinputGetState!.OriginalFunction(self, size, data);
        if (hr >= 0 && InputBlocked && data != null) new Span<byte>(data, (int)size).Clear();
        return hr;
    }

    void HookWindow(nint hwnd)
    {
        if (hwnd == 0 || hwnd == _hookedWindow) return;
        _wndProc = WndProcImpl;
        _keepAlive.Add(_wndProc);
        _wndProcPrev = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
        _hookedWindow = hwnd;
        _log.Info($"filtering keyboard messages of window 0x{hwnd:X}");
    }

    nint WndProcImpl(nint hwnd, uint msg, nint w, nint l)
    {
        if (InputBlocked)
        {
            bool altF4 = msg == WM_SYSKEYDOWN && (int)w == 0x73;
            if (!altF4 && msg is WM_KEYDOWN or WM_KEYUP or WM_CHAR or WM_SYSKEYDOWN or WM_SYSKEYUP) return 0;
            if (msg == WM_INPUT) return DefWindowProcW(hwnd, msg, w, l); // lets Windows release the raw input
        }
        return CallWindowProcW(_wndProcPrev, hwnd, msg, w, l);
    }

    /// <summary>Keyboard hotkeys, polled once per frame on the render thread while the game window has focus.</summary>
    public void PollKeyboard(nint gameWindow)
    {
        if (gameWindow == 0 || GetForegroundWindow() != gameWindow) { _keysDown.Clear(); return; }
        foreach (var (id, vk) in _keys)
        {
            bool down = KeyDownReal(vk);
            if (down && _keysDown.Add(vk)) Hotkeys.Enqueue(id);
            else if (!down) _keysDown.Remove(vk);
        }
    }

    // ------------------------------------------------------------------ Garu macro

    public void StartMacro(AutoPullMacro macro, double timeScale)
    {
        lock (_macroGate) { _macro = macro; _macroStart = RealSeconds; _macroScale = timeScale; }
    }

    (bool grab, bool back) MacroButtons()
    {
        lock (_macroGate)
        {
            if (_macro == null) return (false, false);
            var (grab, back, done) = _macro.StateAt((RealSeconds - _macroStart) * 1000 * _macroScale);
            if (done) { _macro = null; MacroFinished.Enqueue(true); }
            return (grab, back);
        }
    }

    /// <summary>Keyboard players: press Catherine's grab/back keys for the macro (pad players get them through XInput).</summary>
    void DriveMacroKeys()
    {
        bool usePad = PadSeen && _xinput != null;
        var (grab, back) = usePad ? (false, false) : MacroButtons();
        if (grab != _sentGrab) { SendKey(_grabVk, grab); _sentGrab = grab; }
        if (back != _sentBack) { SendKey(_backVk, back); _sentBack = back; }
    }

    // ------------------------------------------------------------------ D3D9

    int PresentImpl(nint dev, nint src, nint dst, nint wnd, nint dirty)
    {
        try
        {
            HookWindow(Overlay.D3D9.FocusWindow(dev));
            if (_xinput == null) TryHookXInput();
            DrainFileLog();
            DriveMacroKeys();
            OnFrame?.Invoke(dev);
        }
        catch (Exception e) { _log.Error($"frame: {e}"); }
        return _present!.OriginalFunction(dev, src, dst, wnd, dirty);
    }

    int ResetImpl(nint dev, nint pp)
    {
        try { OnDeviceReset?.Invoke(); } catch (Exception e) { _log.Error($"reset: {e.Message}"); }
        return _reset!.OriginalFunction(dev, pp);
    }

    void DrainFileLog()
    {
        while (FileOpenLog.TryDequeue(out var line)) _log.Info(line);
    }
}
