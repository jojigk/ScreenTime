using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenTimeAgent.Idle;
using ScreenTimeAgent.Ipc;
using ScreenTimeAgent.Overlay;

namespace ScreenTimeAgent;

/// <summary>
/// No normal window — the agent runs invisibly in the background until the countdown overlay
/// is needed, so shutdown must be explicit rather than tied to a main window closing.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var dispatcher = Dispatcher.CurrentDispatcher;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IIdleTimeProvider, Win32IdleTimeProvider>();
        builder.Services.AddSingleton(dispatcher);
        builder.Services.AddSingleton<IOverlayController, OverlayController>();
        builder.Services.AddHostedService<PipeClientHostedService>();

        _host = builder.Build();
        _ = _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _ = _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
