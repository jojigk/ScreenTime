using ScreenTimeService.Agent;
using ScreenTimeService.Config;
using ScreenTimeService.Ipc;
using ScreenTimeService.Locking;
using ScreenTimeService.Quota;
using ScreenTimeService.State;
using ScreenTimeService.Time;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ScreenTimeService";
});

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<QuotaConfigStore>();
builder.Services.AddSingleton<UsageStateStore>();
builder.Services.AddSingleton<InteractiveProcessLauncher>();
builder.Services.AddSingleton<SessionLockService>();

builder.Services.AddSingleton(sp => sp.GetRequiredService<QuotaConfigStore>().LoadOrCreate());
builder.Services.AddSingleton<UsageTrackerRegistry>();

builder.Services.AddHostedService<UsageTrackerHostedService>();
builder.Services.AddHostedService<PipeServerHostedService>();
builder.Services.AddHostedService<AgentSupervisorHostedService>();

var host = builder.Build();
host.Run();
