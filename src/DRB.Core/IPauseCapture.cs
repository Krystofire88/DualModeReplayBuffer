namespace DRB.Core;

/// <summary>Pauses capture when overlay is open. OverlayWindow calls Pause/Resume.</summary>
public interface IPauseCapture
{
    bool IsPaused { get; }
    void Pause();
    void Resume();
}
