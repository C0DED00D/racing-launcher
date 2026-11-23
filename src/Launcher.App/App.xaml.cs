using System.Windows;
using System.Windows.Threading;
using Launcher.Core;
using Launcher.Core.Config;

namespace Launcher.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal background exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        LauncherConfig config;
        try
        {
            var root = Paths.FindRoot();
            config = LauncherConfig.Load(root);
            Log.Init(config.RuntimeDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Racing Launcher: config problem", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // The wheels (virtual pads + force feedback) come from the wheel hub; start it before the menu reads pads.
        Launcher.Core.Wheels.WheelHubClient.EnsureRunning(config);

        var window = new MainWindow(config);
        MainWindow = window;
        window.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        (MainWindow as MainWindow)?.ShowStatus("Something went wrong: " + e.Exception.Message, isError: true);
    }
}
