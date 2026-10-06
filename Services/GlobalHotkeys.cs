using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace InstaDesktop.Services;

// A key combination such as "Ctrl+Alt+I". Needs Ctrl, Alt or Win (Shift alone
// would steal ordinary typing) plus one non-modifier key.
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    private static readonly (ModifierKeys Flag, string Name)[] Names =
        { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win") };

    public bool IsValid =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 && !IsModifier(Key) &&
        Key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed);

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public override string ToString()
    {
        var modifiers = Modifiers;
        return string.Join("+", Names.Where(n => modifiers.HasFlag(n.Flag)).Select(n => n.Name).Append(KeyName(Key)));
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        _ => key.ToString()
    };

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            var name = Names.FirstOrDefault(n => string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (name.Name is null) return null;
            modifiers |= name.Flag;
        }
        string last = parts[^1];
        Key key;
        if (last.Length == 1 && char.IsDigit(last[0])) key = Key.D0 + (last[0] - '0');
        else if (last.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && last.Length == 4 && char.IsDigit(last[3])) key = Key.NumPad0 + (last[3] - '0');
        else if (!Enum.TryParse(last, true, out key) || int.TryParse(last, out _)) return null;
        var hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid ? hotkey : null;
    }
}

// System-wide shortcuts registered on one window; Windows reports a press
// with WM_HOTKEY and the id. Registration fails if another app owns the keys.
internal sealed class GlobalHotkeys : IDisposable
{
    public const int WmHotkey = 0x0312;
    private readonly IntPtr _window;
    private readonly HashSet<int> _registered = new();

    public GlobalHotkeys(IntPtr window) => _window = window;

    public bool Register(int id, Hotkey? hotkey)
    {
        Unregister(id);
        if (hotkey is not { IsValid: true } key) return true; // off is not a failure
        uint modifiers = 0x4000; // MOD_NOREPEAT
        if (key.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 0x1;
        if (key.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 0x2;
        if (key.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 0x4;
        if (key.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 0x8;
        if (!RegisterHotKey(_window, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key.Key))) return false;
        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) UnregisterHotKey(_window, id);
    }

    public void Dispose()
    {
        foreach (int id in _registered.ToArray()) Unregister(id);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
