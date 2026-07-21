namespace BotDs.Input;

/// <summary>
/// Shared virtual-key name resolution used by key sinks and emergency hotkeys.
/// </summary>
public static class VirtualKeyMap
{
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12; // Alt

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;

    private static readonly Dictionary<string, ushort> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
        ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
        ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
        ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
        ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
        ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
        ["Z"] = 0x5A,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62,
        ["Numpad3"] = 0x63, ["Numpad4"] = 0x64, ["Numpad5"] = 0x65,
        ["Numpad6"] = 0x66, ["Numpad7"] = 0x67, ["Numpad8"] = 0x68,
        ["Numpad9"] = 0x69,
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D,
        ["Escape"] = 0x1B, ["Esc"] = 0x1B,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["Shift"] = VkShift, ["Ctrl"] = VkControl, ["Alt"] = VkMenu,
    };

    public static ushort? Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (Map.TryGetValue(key.Trim(), out ushort vk))
            return vk;
        return null;
    }

    /// <summary>
    /// Parse a binding like "Ctrl+Shift+F12" into RegisterHotKey modifiers + virtual key.
    /// Requires at least one modifier for emergency-stop safety.
    /// </summary>
    public static bool TryParseHotkey(string? binding, out uint modifiers, out ushort virtualKey, out string? error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(binding))
        {
            error = "Hotkey binding is required.";
            return false;
        }

        var parsed = KeyBindingGrammar.Parse(binding);
        if (parsed is null)
        {
            error = $"Hotkey '{binding}' has invalid format.";
            return false;
        }

        if (parsed.Value.Modifiers.Count == 0)
        {
            error = $"Hotkey '{binding}' must include at least one modifier (Ctrl/Shift/Alt).";
            return false;
        }

        ushort? vk = Resolve(parsed.Value.Key);
        if (vk is null)
        {
            error = $"Hotkey '{binding}' uses unrecognized key '{parsed.Value.Key}'.";
            return false;
        }

        foreach (string mod in parsed.Value.Modifiers)
        {
            if (string.Equals(mod, "Ctrl", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModControl;
            else if (string.Equals(mod, "Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModShift;
            else if (string.Equals(mod, "Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModAlt;
            else
            {
                error = $"Hotkey '{binding}' uses unsupported modifier '{mod}'.";
                return false;
            }
        }

        modifiers |= ModNoRepeat;
        virtualKey = vk.Value;
        return true;
    }

    /// <summary>
    /// True when a combat profile binding collides with the emergency hotkey
    /// (same target key and identical modifier set).
    /// </summary>
    public static bool Collides(string? profileBinding, string? emergencyHotkey)
    {
        var a = KeyBindingGrammar.Parse(profileBinding ?? string.Empty);
        var b = KeyBindingGrammar.Parse(emergencyHotkey ?? string.Empty);
        if (a is null || b is null)
            return false;

        if (!string.Equals(a.Value.Key, b.Value.Key, StringComparison.OrdinalIgnoreCase))
            return false;

        if (a.Value.Modifiers.Count != b.Value.Modifiers.Count)
            return false;

        foreach (string mod in a.Value.Modifiers)
        {
            if (!b.Value.Modifiers.Contains(mod))
                return false;
        }

        return true;
    }
}
