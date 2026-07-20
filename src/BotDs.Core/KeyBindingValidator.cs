using System.Text.RegularExpressions;

namespace BotDs.Core;

/// <summary>
/// Validates key binding strings used in combat profiles.
/// Enforces a strict grammar: optional modifiers separated by '+', followed by a key.
/// </summary>
public static partial class KeyBindingValidator
{
    // Allowed modifiers
    private static readonly HashSet<string> AllowedModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shift", "Ctrl", "Alt",
    };

    // Well-known virtual key names
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Numbers
        "0","1","2","3","4","5","6","7","8","9",
        // Letters
        "A","B","C","D","E","F","G","H","I","J","K","L","M",
        "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
        // Function keys
        "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
        // Navigation
        "Left","Right","Up","Down",
        "Home","End","PageUp","PageDown",
        // Editing
        "Insert","Delete","Backspace","Tab","Escape","Space",
        // Numpad
        "NumPad0","NumPad1","NumPad2","NumPad3","NumPad4",
        "NumPad5","NumPad6","NumPad7","NumPad8","NumPad9",
        "NumPadPlus","NumPadMinus","NumPadMultiply","NumPadDivide","NumPadDecimal",
        // Symbols
        "Oem1","Oem2","Oem3","Oem4","Oem5","Oem6","Oem7","Oem8",
        "OemComma","OemPeriod","OemMinus","OemPlus","OemTilde",
        // Modifier keys themselves (rare but valid as standalone binds)
        "ShiftKey","ControlKey","Menu",
        // Misc
        "Apps","PrintScreen","Scroll","Pause","Capital",
    };

    // Pattern: optional modifiers separated by '+', then a key token.
    // Supports: Key, Mod+Key, Mod+Mod+Key
    [GeneratedRegex(@"^(?:[A-Za-z0-9]+\+)*[A-Za-z0-9]+$", RegexOptions.Compiled)]
    private static partial Regex BindingPattern();

    /// <summary>
    /// Validates a key binding string. Returns null on success, or an error message.
    /// </summary>
    public static string? Validate(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
            return "Key binding is required.";

        string trimmed = binding.Trim();

        if (!BindingPattern().IsMatch(trimmed))
            return $"Key binding '{trimmed}' has invalid format. Expected e.g. '1', 'Shift+F1', 'Ctrl+Shift+X'.";

        string[] parts = trimmed.Split('+');
        if (parts.Length > 4)
            return $"Key binding '{trimmed}' has too many parts (max 3 modifiers + 1 key).";

        // Last part is the key; all preceding are modifiers
        string key = parts[^1];
        string[] modifiers = parts[..^1];

        if (!AllowedKeys.Contains(key))
            return $"Key binding '{trimmed}' uses unrecognized key '{key}'.";

        var seenModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mod in modifiers)
        {
            if (!AllowedModifiers.Contains(mod))
                return $"Key binding '{trimmed}' uses unrecognized modifier '{mod}'. Allowed: Shift, Ctrl, Alt.";
            if (!seenModifiers.Add(mod))
                return $"Key binding '{trimmed}' uses duplicate modifier '{mod}'.";
        }

        // Reject bindings that collide with the emergency stop hotkey pattern (at least one modifier present)
        // The exact hotkey is configurable, but we enforce that any single-key binding without modifiers
        // is valid. The emergency stop hotkey check happens at arm time.
        return null;
    }

    /// <summary>
    /// Validates all key bindings in a combat profile, returning all errors.
    /// </summary>
    public static IReadOnlyList<string> ValidateAll(CombatProfile profile)
    {
        var errors = new List<string>();

        if (profile.Abilities is null)
            return errors;

        foreach ((string alias, AbilityBinding binding) in profile.Abilities)
        {
            if (binding is null || !binding.Enabled)
                continue;

            string? err = Validate(binding.Key);
            if (err is not null)
                errors.Add($"Ability '{alias}': {err}");
        }

        return errors;
    }
}
