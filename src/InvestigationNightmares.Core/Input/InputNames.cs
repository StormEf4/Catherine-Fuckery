using System;
using System.Collections.Generic;

namespace InvestigationNightmares.Input;

/// <summary>Turns the binding sheet's names into XInput button masks and Windows virtual-key codes.</summary>
public static class InputNames
{
    static readonly Dictionary<string, ushort> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DPAD_UP"] = 0x0001, ["DPAD_DOWN"] = 0x0002, ["DPAD_LEFT"] = 0x0004, ["DPAD_RIGHT"] = 0x0008,
        ["START"] = 0x0010, ["BACK"] = 0x0020, ["LEFT_THUMB"] = 0x0040, ["RIGHT_THUMB"] = 0x0080,
        ["LEFT_SHOULDER"] = 0x0100, ["RIGHT_SHOULDER"] = 0x0200,
        ["A"] = 0x1000, ["B"] = 0x2000, ["X"] = 0x4000, ["Y"] = 0x8000,
    };

    public static ushort ButtonMask(string combo)
    {
        ushort mask = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Buttons.TryGetValue(part, out var b)) throw new FormatException($"unknown XInput button '{part}'");
            mask |= b;
        }
        if (mask == 0) throw new FormatException("empty button combo");
        return mask;
    }

    public static int VirtualKey(string name)
    {
        if (!name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)) throw new FormatException($"'{name}' is not a VK_ name");
        var k = name[3..].ToUpperInvariant();
        if (k.Length == 1 && (char.IsAsciiLetterUpper(k[0]) || char.IsAsciiDigit(k[0]))) return k[0];
        return k switch
        {
            "BACK" => 0x08, "TAB" => 0x09, "RETURN" => 0x0D, "SHIFT" => 0x10, "CONTROL" => 0x11, "MENU" => 0x12,
            "ESCAPE" => 0x1B, "SPACE" => 0x20, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            _ when k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24 => 0x6F + f,
            _ => throw new FormatException($"unknown virtual key '{name}'"),
        };
    }
}

/// <summary>
/// Turns raw pad buttons into mod hotkey presses (rising edges of a full combo) and decides which buttons
/// the game should see. The modifier (the button every multi-button hotkey shares, Back by default) is held
/// back from the game while it is down; if it is released without completing a hotkey, the game gets it as a
/// short tap instead, so its own use of that button still works. A combo's buttons stay hidden until released.
/// </summary>
public sealed class ComboTracker
{
    const int TapPolls = 3;
    readonly (string id, ushort mask)[] _combos;
    readonly ushort _modifier;
    ushort _hidden;
    ushort _prev;
    bool _modifierUsed;
    int _tapPollsLeft;

    public ComboTracker(IEnumerable<(string id, ushort mask)> combos, ushort modifier)
    {
        _combos = new List<(string, ushort)>(combos).ToArray();
        _modifier = modifier;
    }

    /// <summary>The modifier is the first button of the multi-button combos ("BACK+Y" -> BACK).</summary>
    public static ushort ModifierOf(IEnumerable<string> comboNames)
    {
        ushort mod = 0;
        foreach (var name in comboNames)
        {
            var parts = name.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var first = InputNames.ButtonMask(parts[0]);
            if (mod != 0 && mod != first) throw new FormatException("all multi-button hotkeys must start with the same modifier button");
            mod = first;
        }
        return mod;
    }

    public ushort Modifier => _modifier;

    /// <summary>Feed this poll's buttons; returns the hotkeys that fired and the buttons to pass to the game.</summary>
    public (List<string> fired, ushort forGame) Poll(ushort buttons)
    {
        var fired = new List<string>();
        foreach (var (id, mask) in _combos)
        {
            bool down = (buttons & mask) == mask, wasDown = (_prev & mask) == mask;
            if (down && !wasDown) fired.Add(id);
            if (down && System.Numerics.BitOperations.PopCount(mask) > 1) { _hidden |= mask; _modifierUsed = true; }
        }
        bool modDown = _modifier != 0 && (buttons & _modifier) == _modifier;
        bool modWasDown = _modifier != 0 && (_prev & _modifier) == _modifier;
        if (modWasDown && !modDown)
        {
            if (!_modifierUsed) _tapPollsLeft = TapPolls;
            _modifierUsed = false;
        }
        _hidden &= buttons;
        _prev = buttons;

        ushort forGame = (ushort)(buttons & ~_hidden);
        if (modDown) forGame &= (ushort)~_modifier;
        if (_tapPollsLeft > 0) { _tapPollsLeft--; forGame |= _modifier; }
        return (fired, forGame);
    }
}
