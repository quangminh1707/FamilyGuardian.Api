using System.Net;
using System.Net.Sockets;
using FamilyGuardian.Api.Services;

namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Forward proxy server chạy trên port 8888.
/// Chạy như IHostedService trong ASP.NET Core app.
/// </summary>
public class ProxyServer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ProxyServer> _logger;
    private TcpListener? _listener;

    public ProxyServer(IServiceScopeFactory scopeFactory, IConfiguration config,
        ILogger<ProxyServer> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Proxy:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Proxy server is disabled.");
            return;
        }

        var port = _config.GetValue<int>("Proxy:Port", 8888);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _logger.LogInformation("Proxy server started on port {Port}", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                // Handle each client in a separate task (don't await)
                _ = Task.Run(async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<ProxyConnectionHandler>();
                    await handler.HandleAsync(client, stoppingToken);
                }, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proxy server error accepting connection");
            }
        }

        _listener.Stop();
        _logger.LogInformation("Proxy server stopped.");
    }
}
