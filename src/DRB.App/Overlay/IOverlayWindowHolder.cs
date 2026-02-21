namespace DRB.App.Overlay;

public interface IOverlayWindowHolder
{
    OverlayWindow? Window { get; }
    void Set(OverlayWindow window);
    void WhenReady(Action<OverlayWindow> callback);
}
