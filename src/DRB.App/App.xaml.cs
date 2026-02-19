using System.Windows;
using Application = System.Windows.Application;

namespace DRB.App;

public partial class App : Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }
}
