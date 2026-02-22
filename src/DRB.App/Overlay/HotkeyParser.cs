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
        
        // Normalize: WPF Key enum outputs e.g. "NumPad0" -> "NUMPAD0"
        var normalized = name.ToUpperInvariant().Replace(" ", "");
        
        return normalized switch
        {
            // Numpad
            "NUMPAD0" => 0x60,
            "NUMPAD1" => 0x61,
            "NUMPAD2" => 0x62,
            "NUMPAD3" => 0x63,
            "NUMPAD4" => 0x64,
            "NUMPAD5" => 0x65,
            "NUMPAD6" => 0x66,
            "NUMPAD7" => 0x67,
            "NUMPAD8" => 0x68,
            "NUMPAD9" => 0x69,
            "MULTIPLY" => 0x6A,
            "ADD" => 0x6B,
            "SUBTRACT" => 0x6D,
            "DECIMAL" => 0x6E,
            "DIVIDE" => 0x6F,
            // Arrow keys
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            // Extra keys
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PRIOR" => 0x21,
            "PAGEDOWN" or "NEXT" => 0x22,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "TAB" => 0x09,
            "CAPSLOCK" => 0x14,
            // OEM keys - WPF Key enum names
            "OEM3" or "OEM_3" or "OEMTILDE" or "OEMTILDE" or "BACKTICK" or "`" => 0xC0,  // ` ~
            "OEM1" or "OEMSEMICOLON" => 0xBA,   // ; :
            "OEM2" or "OEMQUESTION"  => 0xBF,   // / ?
            "OEM4" or "OEMOPENBRACKETS" => 0xDB, // [ {
            "OEM5" or "OEMPIPE"      => 0xDC,   // \ |
            "OEM6" or "OEMCLOSEBRACKETS" => 0xDD,// ] }
            "OEM7" or "OEMQUOTES"    => 0xDE,   // ' "
            "OEMCOMMA" or "OEM188"   => 0xBC,   // , <
            "OEMPERIOD" or "OEM190"  => 0xBE,   // . >
            "OEMMINUS" or "OEM189"   => 0xBD,   // - _
            "OEMPLUS" or "OEM187"    => 0xBB,   // = +
            // Function keys
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            // Common keys
            "ENTER" => 0x0D, "ESCAPE" or "ESC" => 0x1B, "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            _ => 0
        };
    }
}
