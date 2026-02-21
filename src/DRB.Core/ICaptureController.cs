namespace DRB.Core;

/// <summary>Controls capture worker start/stop (power ON/OFF). Overlay ON/OFF toggle uses this.</summary>
public interface ICaptureController
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}
