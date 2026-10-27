namespace Retrace.Core;

// NO AutoCAD, WPF or Win32 references in this file (linked into the engine test project). Parses the `checkpointKey` setting.

/// <summary>A keyboard shortcut: Ctrl / Shift / Alt plus one key, kept as a Windows virtual-key code.</summary>
public readonly record struct KeyChord(bool Ctrl, bool Shift, bool Alt, int Vk)
{
    /// <summary>The shortcut when the setting is missing.</summary>
    public const string Default = "Ctrl+Shift+K";

    /// <summary>Canonical text: modifiers in the order Ctrl, Shift, Alt, then the key (`Ctrl+Shift+K`, `Alt+F7`).</summary>
    public string Format() => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + KeyName(Vk);

    /// <summary>"" and "None" (any case) mean no shortcut.</summary>
    public static bool IsOff(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length == 0 || t.Equals("None", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads `shift+ctrl+k`, `Ctrl+F7`, `F11`, `Ctrl+Shift+1`. Keys: A-Z, 0-9, F1-F24. A letter or digit needs Ctrl (alone it would eat
    /// typing); an F-key may stand alone. Alt is refused: AutoCAD shows the ribbon key tips when it is let go, as the shortcut's own key press
    /// is taken away. On failure <paramref name="error"/> says why, in plain words.
    /// </summary>
    public static bool TryParse(string? text, out KeyChord chord, out string error)
    {
        chord = default; error = "";
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts[^1].Length == 0) { error = "Type a key such as Ctrl+Shift+K."; return false; }
        bool ctrl = false, shift = false, alt = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var name = parts[i].ToLowerInvariant();
            bool seen;
            if (name is "ctrl" or "control") { seen = ctrl; ctrl = true; }
            else if (name == "shift") { seen = shift; shift = true; }
            else if (name == "alt") { error = "Alt cannot be used: letting it go opens the ribbon's key tips. Use Ctrl or Shift."; return false; }
            else { error = $"\"{parts[i]}\" is not Ctrl or Shift."; return false; }
            if (seen) { error = $"\"{parts[i]}\" is given twice."; return false; }
        }
        var key = parts[^1];
        int vk = KeyCode(key);
        if (vk == 0) { error = $"\"{key}\" is not a key Retrace can use. Use A-Z, 0-9 or F1-F24."; return false; }
        if (vk < 0x70 && !ctrl) { error = "A letter or digit needs Ctrl, or it would block typing."; return false; }
        chord = new KeyChord(ctrl, shift, alt, vk);
        return true;
    }

    /// <summary>The shortcut a stored setting means: null when it is off; the default when it is invalid (<paramref name="warning"/> then says so).</summary>
    public static KeyChord? Resolve(string? setting, out string warning)
    {
        warning = "";
        if (IsOff(setting)) return null;
        if (TryParse(setting, out var c, out var err)) return c;
        warning = $"checkpointKey \"{setting}\" is not valid: {err} Using {Default}.";
        TryParse(Default, out c, out _);
        return c;
    }

    static int KeyCode(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            return c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : 0;
        }
        if (key.Length > 1 && key[0] is 'F' or 'f'
            && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
            return 0x70 + n - 1;
        return 0;
    }

    /// <summary>Name of a virtual-key code that <see cref="TryParse"/> accepts.</summary>
    public static string KeyName(int vk) => vk >= 0x70 ? "F" + (vk - 0x70 + 1) : ((char)vk).ToString();
}
