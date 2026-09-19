using Microsoft.UI.Xaml;

namespace NasToolbox;

public partial class App : Application
{
    public static Window? MainWin { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWin = new MainWindow();
        MainWin.Activate();
    }
}