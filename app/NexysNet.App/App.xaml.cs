using System.Windows;
using NexysNet.App.Services;

namespace NexysNet.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Diag.Trace($"net start args=[{string.Join(" ", e.Args)}] pid={Environment.ProcessId}");
        DispatcherUnhandledException += (_, ex) => { Diag.Trace("UNHANDLED " + ex.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Diag.Trace("UNHANDLED(domain) " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Diag.Trace("UNOBSERVED " + ex.Exception); ex.SetObserved(); };
        var win = new MainWindow();
        MainWindow = win;
        Diag.StartWatchdog(win.Dispatcher);
        if (SelfTest.Enabled) win.WhenReady.ContinueWith(_ => win.Dispatcher.BeginInvoke(async () => await SelfTest.RunAsync(win)));
        else win.WhenReady.ContinueWith(_ => win.Dispatcher.BeginInvoke(() =>
        {
            foreach (var a in e.Args) if (!a.StartsWith("--") && System.IO.File.Exists(a)) win.Engine.OpenAny(a);
        }));
        win.Show();
    }
}
