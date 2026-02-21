namespace DRB.App.Overlay;

public sealed class OverlayWindowHolder : IOverlayWindowHolder
{
    private OverlayWindow? _window;
    private readonly List<Action<OverlayWindow>> _pendingCallbacks = [];
    private readonly object _lock = new();
    private Action? _refreshHotkeysCallback;

    // Static reference for easy access
    private static OverlayWindowHolder? _instance;
    public static OverlayWindowHolder? Instance => _instance;

    public OverlayWindowHolder()
    {
        _instance = this;
    }

    public OverlayWindow? Window => _window;

    public void Set(OverlayWindow window)
    {
        lock (_lock)
        {
            _window = window;
            foreach (var cb in _pendingCallbacks)
                cb(window);
            _pendingCallbacks.Clear();
        }
    }

    public void WhenReady(Action<OverlayWindow> callback)
    {
        lock (_lock)
        {
            if (_window is not null)
            {
                callback(_window);
            }
            else
            {
                _pendingCallbacks.Add(callback);
            }
        }
    }

    public void SetRefreshHotkeysCallback(Action callback)
    {
        _refreshHotkeysCallback = callback;
    }

    public void RefreshHotkeys()
    {
        _refreshHotkeysCallback?.Invoke();
    }
}
