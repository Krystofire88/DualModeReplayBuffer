using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DRB.App.Overlay;

public partial class HotkeyWindow : Window
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int HotkeyId = 0xC001;

    public event Action? HotkeyPressed;

    private HwndSource? _source;
    private bool _registered;

    public HotkeyWindow()
    {
        InitializeComponent();
    }

    /// <summary>Ensures the window has an HWND and installs the WndProc hook. Call before RegisterHotKey.</summary>
    public void PrepareForHotkey()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        IntPtr hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle
    {
        get
        {
            var helper = new WindowInteropHelper(this);
            return helper.Handle;
        }
    }

    public bool TryRegister(uint modifiers, uint vk)
    {
        if (_source == null || Handle == IntPtr.Zero) return false;
        if (_registered) Unregister();
        _registered = RegisterHotKey(Handle, HotkeyId, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (Handle == IntPtr.Zero || !_registered) return;
        UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312) // WM_HOTKEY
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }
}
