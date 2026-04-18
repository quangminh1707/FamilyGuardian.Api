using System.Net;
using System.Text;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Models.Entities;

namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Family Guardian Proxy Server dùng Titanium.Web.Proxy
/// - Intercept HTTP và HTTPS (MITM SSL)
/// - Chặn domain không có trong allowed_websites của child
/// - Ghi nhận session truy cập để tính thời gian
/// </summary>
public class FamilyProxyServer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<FamilyProxyServer> _logger;
    private ProxyServer? _proxy;

    public FamilyProxyServer(IServiceScopeFactory scopeFactory, IConfiguration config,
        ILogger<FamilyProxyServer> logger)
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
            _logger.LogInformation("Proxy server is disabled");
            return;
        }

        try
        {
            var port = _config.GetValue<int>("Proxy:Port", 8888);

            _proxy = new ProxyServer(
                userTrustRootCertificate: true,     // auto trust root cert trên Windows
                machineTrustRootCertificate: false
            );

            // Tạo root CA để sign certificate giả cho HTTPS
            _proxy.CertificateManager.RootCertificateName = "Family Guardian CA";
            _proxy.CertificateManager.RootCertificateIssuerName = "Family Guardian";
            
            // Khởi tạo certificate (synchronous)
            _proxy.CertificateManager.CreateRootCertificate();
            _logger.LogInformation("✅ Root certificate initialized");

            // Export cert ra thư mục cố định để AdminController có thể tải
            await ExportRootCertificateAsync();

            // ── Event handlers ──────────────────────────────────
            _proxy.BeforeRequest += OnBeforeRequestAsync;
            _proxy.ServerCertificateValidationCallback += OnCertValidation;

            // ── Endpoint: HTTP + HTTPS (decryptSsl = true = MITM) ──
            var endpoint = new ExplicitProxyEndPoint(IPAddress.Any, port, decryptSsl: true);

            // TRƯỚC khi tạo HTTPS tunnel → check domain ở đây (CONNECT phase)
            // Nếu block ngay lúc này = không cần decrypt SSL (nhanh hơn)
            endpoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectAsync;

            _proxy.AddEndPoint(endpoint);
            _proxy.Start();

            _logger.LogInformation("✅ Titanium Proxy started on port {Port} (HTTP + HTTPS MITM decryption enabled)", port);

            // Chờ cho đến khi app shutdown
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Proxy server stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running proxy server");
        }
        finally
        {
            if (_proxy != null)
            {
                _proxy.BeforeRequest -= OnBeforeRequestAsync;
                _proxy.Stop();
                _proxy.Dispose();
                _logger.LogInformation("Proxy stopped and disposed");
            }
        }
    }

    // ── Xử lý HTTPS CONNECT (trước khi decrypt SSL) ────────────
    // Nếu domain bị block → dừng ngay, không cần decrypt
    private async Task OnBeforeTunnelConnectAsync(object sender, TunnelConnectSessionEventArgs e)
    {
        try
        {
            var hostname = e.HttpClient.Request.RequestUri.Host.ToLower();

            using var scope = _scopeFactory.CreateScope();
            var checker = scope.ServiceProvider.GetRequiredService<IProxyAccessChecker>();

            var clientIp = e.ClientRemoteEndPoint?.Address?.ToString() ?? "unknown";
            var result = await checker.CheckAccessAsync(clientIp, hostname);

            if (result.Decision == AccessDecision.Blocked)
            {
                // Dừng CONNECT tunnel → browser nhận lỗi kết nối
                e.DenyConnect = true;
                _logger.LogDebug("CONNECT blocked: {Host} for IP {IP} — {Reason}",
                    hostname, clientIp, result.Reason);

                // Ghi log bị chặn
                _ = LogAccessAsync(result.ChildId, hostname, null,
                    AccessResult.Blocked, null, scope.ServiceProvider);
            }
            else
            {
                // Cho phép decrypt SSL để theo dõi từng request bên trong
                e.DecryptSsl = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnBeforeTunnelConnectAsync");
        }
    }

    // ── Xử lý mọi HTTP/HTTPS request (sau khi decrypt) ─────────
    private async Task OnBeforeRequestAsync(object sender, SessionEventArgs e)
    {
        try
        {
            var request = e.HttpClient.Request;
            var hostname = request.RequestUri.Host.ToLower();

            // Bỏ qua request nội bộ của proxy (cert download...)
            if (hostname == "localhost" || hostname == "127.0.0.1") return;

            using var scope = _scopeFactory.CreateScope();
            var checker = scope.ServiceProvider.GetRequiredService<IProxyAccessChecker>();
            var tracker = scope.ServiceProvider.GetRequiredService<ISessionTracker>();

            var clientIp = e.ClientRemoteEndPoint?.Address?.ToString() ?? "unknown";
            var result = await checker.CheckAccessAsync(clientIp, hostname);

            if (result.Decision == AccessDecision.Blocked)
            {
                // Trả về trang block HTML
                var html = GenerateBlockPage(hostname, result.Reason);
                var htmlBytes = Encoding.UTF8.GetBytes(html);

                e.Ok(htmlBytes, new Dictionary<string, HttpHeader>
                {
                    { "Content-Type", new HttpHeader("Content-Type", "text/html; charset=utf-8") }
                });

                // Ghi log bị chặn
                _ = LogAccessAsync(result.ChildId, hostname, request.Url,
                    AccessResult.Blocked, null, scope.ServiceProvider);

                _logger.LogDebug("HTTP blocked: {Url} — {Reason}", request.Url, result.Reason);
            }
            else if (result.Decision == AccessDecision.Allowed && result.ChildId.HasValue && result.AllowedWebsiteId.HasValue)
            {
                // Ghi nhận session (tracking thời gian)
                await tracker.RecordRequestAsync(
                    result.ChildId.Value,
                    result.AllowedWebsiteId.Value,
                    DomainNormalizer.Normalize(hostname),
                    clientIp);

                // Ghi log được phép
                _ = LogAccessAsync(result.ChildId, hostname, request.Url,
                    AccessResult.Allowed, result.AllowedWebsiteId, scope.ServiceProvider);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnBeforeRequestAsync");
        }
    }

    // Bỏ qua lỗi certificate của server (tránh proxy crash khi server có cert tự ký)
    private Task OnCertValidation(object sender, CertificateValidationEventArgs e)
    {
        e.IsValid = true;
        return Task.CompletedTask;
    }

    private async Task LogAccessAsync(int? childId, string domain, string? url,
        AccessResult result, int? websiteId, IServiceProvider services)
    {
        if (childId == null) return;
        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.WebAccessLogs.Add(new WebAccessLog
            {
                ChildId = childId.Value,
                Domain = domain,
                FullUrl = url?.Length > 2000 ? url[..2000] : url,
                AccessResult = result,
                AllowedWebsiteId = websiteId,
                SessionStart = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log access for {Domain}", domain);
        }
    }

    /// <summary>
    /// Export root certificate ra file cố định để AdminController có thể tải xuống
    /// </summary>
    private async Task ExportRootCertificateAsync()
    {
        try
        {
            // Thư mục để lưu cert
            var certDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "certs");
            Directory.CreateDirectory(certDir);

            var certPath = Path.Combine(certDir, "FamilyGuardian-RootCA.pfx");

            // Titanium lưu root cert trong CertificateManager.RootCertificate
            if (_proxy?.CertificateManager?.RootCertificate != null)
            {
                // Export cert với password
                var certBytes = _proxy.CertificateManager.RootCertificate
                    .Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "familyguardian");

                await System.IO.File.WriteAllBytesAsync(certPath, certBytes);
                _logger.LogInformation("✅ Root certificate exported to {Path}", certPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to export root certificate");
        }
    }

    private string GenerateBlockPage(string domain, string reason)
    {
        return $@"
<!DOCTYPE html>
<html lang=""vi"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>Trang web bị chặn</title>
  <style>
    *{{margin:0;padding:0;box-sizing:border-box}}
    body{{
      font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
      min-height:100vh;display:flex;align-items:center;justify-content:center;
      background:linear-gradient(135deg,#667eea,#764ba2);
    }}
    .card{{
      background:white;padding:48px 40px;border-radius:24px;
      box-shadow:0 25px 60px rgba(0,0,0,0.3);text-align:center;
      max-width:460px;width:90%;
    }}
    .shield{{font-size:72px;margin-bottom:16px;display:block}}
    h1{{font-size:22px;font-weight:700;color:#1a202c;margin-bottom:8px}}
    .domain{{
      display:inline-block;background:#f0f4ff;color:#5a67d8;
      padding:6px 18px;border-radius:20px;font-weight:600;font-size:16px;margin:12px 0;
    }}
    .reason{{
      background:#fff5f5;border-left:4px solid #fc8181;
      color:#c53030;padding:10px 14px;border-radius:8px;
      font-size:13px;margin:12px 0;text-align:left;
    }}
    p{{color:#718096;font-size:14px;line-height:1.6;margin-top:12px}}
  </style>
</head>
<body>
  <div class=""card"">
    <span class=""shield"">🛡️</span>
    <h1>Trang web bị chặn</h1>
    <div class=""domain"">{domain}</div>
    <div class=""reason"">📋 {reason}</div>
    <p>Trang này không nằm trong danh sách cho phép.<br>
    Liên hệ bố/mẹ nếu bạn cần truy cập trang này.</p>
  </div>
</body>
</html>
";
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping proxy server...");
        if (_proxy != null)
        {
            _proxy.Stop();
            _proxy.Dispose();
        }
        await base.StopAsync(cancellationToken);
    }
}
