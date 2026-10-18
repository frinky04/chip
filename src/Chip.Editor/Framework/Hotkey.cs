using ImGuiNET;

namespace Chip.Editor.Framework;

/// <summary>A keyboard chord. Modifiers must match exactly, so Shift+B and B are different commands.</summary>
public readonly record struct Hotkey(ImGuiKey Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    static readonly Dictionary<string, ImGuiKey> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Del"] = ImGuiKey.Delete, ["Delete"] = ImGuiKey.Delete, ["Esc"] = ImGuiKey.Escape, ["Escape"] = ImGuiKey.Escape,
        ["Enter"] = ImGuiKey.Enter, ["Backspace"] = ImGuiKey.Backspace, ["Space"] = ImGuiKey.Space, ["Tab"] = ImGuiKey.Tab,
        ["["] = ImGuiKey.LeftBracket, ["]"] = ImGuiKey.RightBracket, ["-"] = ImGuiKey.Minus, ["="] = ImGuiKey.Equal,
        ["`"] = ImGuiKey.GraveAccent, ["Left"] = ImGuiKey.LeftArrow, ["Right"] = ImGuiKey.RightArrow,
        ["Up"] = ImGuiKey.UpArrow, ["Down"] = ImGuiKey.DownArrow, ["Home"] = ImGuiKey.Home, ["End"] = ImGuiKey.End,
        ["PageUp"] = ImGuiKey.PageUp, ["PageDown"] = ImGuiKey.PageDown, ["Insert"] = ImGuiKey.Insert,
        ["NumPlus"] = ImGuiKey.KeypadAdd, ["NumMinus"] = ImGuiKey.KeypadSubtract, ["NumEnter"] = ImGuiKey.KeypadEnter,
        ["NumMultiply"] = ImGuiKey.KeypadMultiply, ["NumDivide"] = ImGuiKey.KeypadDivide, ["NumDecimal"] = ImGuiKey.KeypadDecimal,
        [","] = ImGuiKey.Comma, ["."] = ImGuiKey.Period, ["/"] = ImGuiKey.Slash, [";"] = ImGuiKey.Semicolon,
        ["'"] = ImGuiKey.Apostrophe, ["\\"] = ImGuiKey.Backslash, ["CapsLock"] = ImGuiKey.CapsLock, ["Pause"] = ImGuiKey.Pause,
    };

    /// <summary>Parses "Shift+Ctrl+G", "Ctrl+Alt+Num5", "F1", "[", "Del", "Alt+Left", "NumPlus".</summary>
    public static Hotkey Parse(string text)
    {
        bool ctrl = false, shift = false, alt = false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FormatException("Empty hotkey");
        foreach (var mod in parts[..^1])
        {
            if (mod.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (mod.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (mod.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else throw new FormatException($"Unknown modifier '{mod}' in '{text}'");
        }
        return new Hotkey(ParseKey(parts[^1], text), ctrl, shift, alt);
    }

    static ImGuiKey ParseKey(string key, string text)
    {
        if (Named.TryGetValue(key, out var named)) return named;
        if (key.Length == 1 && char.IsLetter(key[0])) return ImGuiKey.A + (char.ToUpperInvariant(key[0]) - 'A');
        if (key.Length == 1 && char.IsDigit(key[0])) return ImGuiKey._0 + (key[0] - '0');
        if (key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && key.Length == 4 && char.IsDigit(key[3])) return ImGuiKey.Keypad0 + (key[3] - '0');
        if (key.StartsWith('F') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 12) return ImGuiKey.F1 + (f - 1);
        // Any other ImGui key by its enum name, so every key a user can capture also round-trips through settings.
        if (char.IsLetter(key[0]) && Enum.TryParse<ImGuiKey>(key, true, out var any) && any is >= ImGuiKey.Tab and < ImGuiKey.GamepadStart) return any;
        throw new FormatException($"Unknown key '{key}' in '{text}'");
    }

    public bool IsPressed()
    {
        var io = ImGui.GetIO();
        return ImGui.IsKeyPressed(Key, false) && io.KeyCtrl == Ctrl && io.KeyShift == Shift && io.KeyAlt == Alt;
    }

    public override string ToString()
    {
        var key = Key;
        var name = key switch
        {
            >= ImGuiKey.A and <= ImGuiKey.Z => ((char)('A' + (Key - ImGuiKey.A))).ToString(),
            >= ImGuiKey._0 and <= ImGuiKey._9 => ((char)('0' + (Key - ImGuiKey._0))).ToString(),
            >= ImGuiKey.Keypad0 and <= ImGuiKey.Keypad9 => $"Num{Key - ImGuiKey.Keypad0}",
            >= ImGuiKey.F1 and <= ImGuiKey.F12 => $"F{Key - ImGuiKey.F1 + 1}",
            _ => Named.FirstOrDefault(kv => kv.Value == key).Key ?? key.ToString(),
        };
        return $"{(Ctrl ? "Ctrl+" : "")}{(Shift ? "Shift+" : "")}{(Alt ? "Alt+" : "")}{name}";
    }
}
