using System.IO;
using System.Windows;
using System.Windows.Threading;

using LiveTranslator.App.Services;
using LiveTranslator.App.Views;

namespace LiveTranslator.App;

public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);

        // --no-captions: manual/testing mode on machines without Windows Live Captions.
        var captionsEnabled = !e.Args.Any(a => a.Equals("--no-captions", StringComparison.OrdinalIgnoreCase));
        _host = new AppHost(Dispatcher, captionsEnabled);

        var main = new MainWindow(_host);
        MainWindow = main;
        main.Show();
        _host.Start();

        if (_host.Store.RecoveredBackupPath is { } backup)
            MessageBox.Show(main, $"设置文件已损坏，已备份到：\n{backup}\n并恢复为默认设置。", "Live Translator",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is { } host)
        {
            // Off the UI thread: disposal awaits background work that must not need the dispatcher.
            Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show($"发生未处理的错误：{e.Exception.Message}\n\n详情已写入 {CrashLogPath}", "Live Translator",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string CrashLogPath => Path.Combine(AppHost.DataDirectory, "error.log");

    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            Directory.CreateDirectory(AppHost.DataDirectory);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
