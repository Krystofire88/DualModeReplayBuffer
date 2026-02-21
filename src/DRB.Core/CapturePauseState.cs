namespace DRB.Core;

/// <summary>Shared state for overlay pause and power control.</summary>
public sealed class CapturePauseState : IPauseCapture, ICaptureController
{
    private volatile bool _isPaused;
    private volatile bool _isRunning = true;

    public bool IsPaused => _isPaused;
    public bool IsRunning => _isRunning;

    public void Pause() => _isPaused = true;
    public void Resume() => _isPaused = false;
    public void Start() => _isRunning = true;
    public void Stop() => _isRunning = false;
}
