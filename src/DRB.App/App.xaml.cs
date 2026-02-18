using System.Windows;

namespace DRB.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }
}

