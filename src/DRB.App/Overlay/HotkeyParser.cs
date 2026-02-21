namespace DRB.App.Overlay;

public static class HotkeyParser
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    public static (uint Modifiers, uint Vk)? Parse(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;
        var parts = hotkey.Trim().Split('+');
        if (parts.Length < 2) return null; // Need at least one modifier + key

        uint mods = 0;
        uint vk = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (string.IsNullOrEmpty(p)) continue;

            if (i < parts.Length - 1)
            {
                // Modifier
                switch (p.ToUpperInvariant())
                {
                    case "ALT": mods |= MOD_ALT; break;
                    case "CTRL":
                    case "CONTROL": mods |= MOD_CONTROL; break;
                    case "SHIFT": mods |= MOD_SHIFT; break;
                    case "WIN":
                    case "WINDOWS": mods |= MOD_WIN; break;
                    default: return null;
                }
            }
            else
            {
                // Virtual key
                vk = KeyNameToVk(p);
                if (vk == 0) return null;
            }
        }

        return mods != 0 && vk != 0 ? (mods, vk) : null;
    }

    public static uint KeyNameToVk(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return (uint)c;
            if (c >= '0' && c <= '9') return (uint)c;
            if (c == '`') return 0xC0;
        }
        return name.ToUpperInvariant() switch
        {
            "`" or "BACKTICK" or "OEM_3" => 0xC0,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "ENTER" => 0x0D, "ESCAPE" or "ESC" => 0x1B, "SPACE" => 0x20,
            _ => 0
        };
    }
}
