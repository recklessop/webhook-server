using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using WebhookServer.Core.Callbacks;
using WebhookServer.Core.Execution;
using WebhookServer.Core.Models;
using WebhookServer.Core.Storage;
using WebhookServer.Service;

[assembly: SupportedOSPlatform("windows")]

Directory.CreateDirectory(ServicePaths.DataRoot);
Directory.CreateDirectory(ServicePaths.LogsDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Async(a => a.File(
        ServicePaths.LogFileTemplate,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
    .CreateLogger();

try
{
    Log.Information("Starting WebhookServer.Service");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseWindowsService(o => o.ServiceName = "WebhookServer");
    builder.Host.UseSerilog();

    var configStore = new ConfigStore(ServicePaths.ConfigPath);
    var initialConfig = await configStore.LoadAsync().ConfigureAwait(false);
    ConfigStore.DecryptSecrets(initialConfig);

    builder.WebHost.ConfigureKestrel(opts =>
    {
        ConfigureHttp(opts, initialConfig);
        ConfigureHttps(opts, initialConfig.HttpsBinding);
    });

    builder.Services.AddSingleton(configStore);
    builder.Services.AddSingleton<ServiceState>();
    builder.Services.AddSingleton<IExecutor, ProcessExecutor>();
    builder.Services.AddSingleton<ConcurrencyGate>();
    builder.Services.AddSingleton<CallbackDispatcher>(sp =>
        new CallbackDispatcher(sp.GetService<Microsoft.Extensions.Logging.ILogger<CallbackDispatcher>>()));
    builder.Services.AddSingleton<WebhookRouter>();
    builder.Services.AddHostedService<CallbackBackgroundService>();
    builder.Services.AddHostedService<AdminPipeServer>();
    builder.Services.AddHostedService<CheckpointScheduler>();

    var app = builder.Build();

    var state = app.Services.GetRequiredService<ServiceState>();
    await state.LoadAsync().ConfigureAwait(false);

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    state.ListenerSettingsChanged += (_, _) =>
    {
        Log.Information("Listener settings changed; stopping service for restart.");
        lifetime.StopApplication();
    };

    // Accept POST (the standard webhook verb) and GET (so a browser can smoke-test
    // hooks without curl). GET requests will have an empty body, which the executor
    // and arg-template renderer handle as if the body were empty JSON.
    app.MapMethods("/hook/{slug}", new[] { "GET", "POST" }, async (string slug, HttpContext http) =>
    {
        var router = http.RequestServices.GetRequiredService<WebhookRouter>();
        await router.HandleAsync(http, slug);
    });

    app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

    // Stop browsers from logging 404s for favicon.ico every time they hit a hook.
    app.MapGet("/favicon.ico", () => Results.StatusCode(StatusCodes.Status204NoContent));

    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static void ConfigureHttp(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions opts, ServerConfig cfg)
{
    if (cfg.BindAddresses is { Count: > 0 } binds)
    {
        foreach (var entry in binds)
        {
            if (System.Net.IPAddress.TryParse(entry, out var ip))
                opts.Listen(ip, cfg.HttpPort);
            else
                Log.Warning("Skipping invalid bind address {Entry}", entry);
        }
    }
    else
    {
        opts.ListenAnyIP(cfg.HttpPort);
    }
}

static void ConfigureHttps(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions opts, HttpsBinding? binding)
{
    if (binding is null || binding.Kind == HttpsBindingKind.None) return;

    X509Certificate2? cert = null;
    switch (binding.Kind)
    {
        case HttpsBindingKind.PfxFile:
            if (string.IsNullOrEmpty(binding.PfxPath)) return;
            var password = binding.PfxPassword?.Plaintext;
            cert = string.IsNullOrEmpty(password)
                ? new X509Certificate2(binding.PfxPath)
                : new X509Certificate2(binding.PfxPath, password);
            break;
        case HttpsBindingKind.CertStoreThumbprint:
            if (string.IsNullOrEmpty(binding.Thumbprint)) return;
            using (var store = new X509Store(StoreName.My, binding.StoreLocation))
            {
                store.Open(OpenFlags.ReadOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, binding.Thumbprint, validOnly: false);
                if (matches.Count > 0) cert = matches[0];
            }
            break;
    }

    if (cert is null)
    {
        Log.Warning("HTTPS binding configured but no certificate was loaded; HTTPS endpoint will not be enabled.");
        return;
    }

    opts.ListenAnyIP(binding.Port, listen => listen.UseHttps(cert));
}
