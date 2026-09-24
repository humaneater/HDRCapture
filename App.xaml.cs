using System.Windows;
using HdrCapture.Core;
using HdrCapture.Infrastructure;

namespace HdrCapture;

public partial class App : System.Windows.Application
{
    private SingleInstance? _singleInstance;
    private AppController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            var console = ConsoleHost.TryAttach();
            if (e.Args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                var exitCode = SelfTestRunner.Run(console);
                Shutdown(exitCode);
                return;
            }

            if (e.Args.Any(arg => string.Equals(arg, "--diagnostics", StringComparison.OrdinalIgnoreCase)))
            {
                var exitCode = await DiagnosticsRunner.RunAsync(console).ConfigureAwait(true);
                Shutdown(exitCode);
                return;
            }

            if (e.Args.Any(arg => string.Equals(arg, "--capture-region", StringComparison.OrdinalIgnoreCase)))
            {
                var exitCode = await RegionCaptureCommand.RunAsync(console, e.Args).ConfigureAwait(true);
                Shutdown(exitCode);
                return;
            }
        }

        _singleInstance = SingleInstance.Acquire();
        if (!_singleInstance.IsOwner)
        {
            System.Windows.MessageBox.Show("HDRCapture 已在运行，请使用托盘图标。", "HDRCapture",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        var openSettings = e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));
        try
        {
            _controller = new AppController();
            _controller.Start();
            if (openSettings)
            {
                _controller.ShowSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Application startup failed.", ex);
            System.Windows.MessageBox.Show($"HDRCapture 启动失败：{ex.Message}", "HDRCapture",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
