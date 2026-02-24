using DRB.App.UI;

namespace DRB.App.Overlay;

public class ThemeService
{
    public bool IsDark { get; private set; } = true; // dark by default
    public event Action<bool>? ThemeChanged;
    
    public void SetTheme(bool isDark)
    {
        IsDark = isDark;
        
        // Also update the static Theme class and fire its change event
        Theme.Apply(isDark);
        
        ThemeChanged?.Invoke(isDark);
    }
}
