using System.Net.Sockets;
using System.Text;
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Proxy;

public class ProxyConnectionHandler
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ProxyConnectionHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    private const string BlockPageTemplate = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="UTF-8">
            <title>Trang web bị chặn</title>
            <style>
                body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; text-align: center; padding: 50px; background: #0f172a; color: #f8fafc; }
                .card { max-width: 500px; margin: 0 auto; padding: 40px; background: #1e293b; border-radius: 16px; box-shadow: 0 10px 25px rgba(0,0,0,0.3); border: 1px solid #334155; }
                h1 { color: #ef4444; margin-bottom: 20px; }
                .domain { font-weight: bold; color: #38bdf8; }
                .reason { margin: 20px 0; padding: 10px; background: #334155; border-radius: 8px; font-style: italic; }
                .footer { margin-top: 30px; color: #94a3b8; font-size: 0.9em; }
            </style>
        </head>
        <body>
          <div class="card">
            <h1>🚫 Truy cập bị chặn</h1>
            <p>Trang web <span class="domain">{domain}</span> đã bị hệ thống Family Guardian chặn.</p>
            <div class="reason">{reason}</div>
            <p>Vui lòng liên hệ bố mẹ để được cấp quyền truy cập.</p>
            <div class="footer">Family Guardian Security Proxy</div>
          </div>
        </body>
        </html>
        """;

    public ProxyConnectionHandler(AppDbContext db, IConfiguration config,
        ILogger<ProxyConnectionHandler> logger, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var requestLine = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;

            var method = parts[0].ToUpperInvariant();
            var url = parts[1];
            var headers = await ReadHeadersAsync(stream, ct);

            // Xác định ChildId qua IP (fallback nếu không có Auth header)
            var clientIp = client.Client.RemoteEndPoint?.ToString()?.Split(':')[0];
            var childId = await GetChildIdAsync(headers, clientIp);

            if (method == "CONNECT")
                await HandleConnectAsync(stream, childId, url, headers, ct);
            else
                await HandleHttpAsync(stream, childId, method, url, requestLine, headers, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Error handling proxy connection");
        }
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, int childId, string targetHostPort,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        var parts = targetHostPort.Split(':');
        var host = parts[0];
        var domain = DomainNormalizer.Normalize(host);
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 443;

        var (decision, websiteId, reason) = await CheckAccessAsync(childId, domain);

        if (decision == "blocked")
        {
            var forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\n\r\n");
            await clientStream.WriteAsync(forbidden, ct);
            await LogAccessAsync(childId, domain, null, "blocked", websiteId, 0);
            return;
        }

        var established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await clientStream.WriteAsync(established, ct);

        var start = DateTime.UtcNow;
        try
        {
            using var targetClient = new TcpClient();
            await targetClient.ConnectAsync(host, port, ct);
            using var targetStream = targetClient.GetStream();

            var t1 = clientStream.CopyToAsync(targetStream, ct);
            var t2 = targetStream.CopyToAsync(clientStream, ct);
            await Task.WhenAny(t1, t2);
        }
        finally
        {
            var duration = (int)(DateTime.UtcNow - start).TotalSeconds;
            await LogAccessAsync(childId, domain, null, "allowed", websiteId, duration);
            if (childId > 0 && websiteId.HasValue && duration > 0)
                await UpsertUsageAsync(childId, websiteId.Value, domain, duration);
        }
    }

    private async Task HandleHttpAsync(NetworkStream clientStream, int childId, string method, string url,
        string requestLine, Dictionary<string, string> headers, CancellationToken ct)
    {
        var host = headers.GetValueOrDefault("host", "");
        var domain = DomainNormalizer.Normalize(host);

        var (decision, websiteId, reason) = await CheckAccessAsync(childId, domain);

        if (decision == "blocked")
        {
            var body = BlockPageTemplate.Replace("{domain}", domain).Replace("{reason}", reason ?? "Không có trong danh sách cho phép");
            var response = BuildHttpResponse(403, "Forbidden", body);
            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
            await LogAccessAsync(childId, domain, url, "blocked", websiteId, 0);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("ProxyForward");
            var message = new HttpRequestMessage(new HttpMethod(method), url);
            foreach (var (k, v) in headers)
            {
                if (k.ToLower() is "host" or "proxy-authorization" or "proxy-connection") continue;
                if (!message.Headers.TryAddWithoutValidation(k, v))
                    message.Content?.Headers.TryAddWithoutValidation(k, v);
            }

            var resp = await client.SendAsync(message, ct);
            var body = await resp.Content.ReadAsByteArrayAsync(ct);

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {(int)resp.StatusCode} {resp.ReasonPhrase}\r\n");
            foreach (var h in resp.Headers) sb.Append($"{h.Key}: {string.Join(", ", h.Value)}\r\n");
            foreach (var h in resp.Content.Headers) sb.Append($"{h.Key}: {string.Join(", ", h.Value)}\r\n");
            sb.Append("\r\n");

            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
            await clientStream.WriteAsync(body, ct);

            await LogAccessAsync(childId, domain, url, "allowed", websiteId, 0);
        }
        catch
        {
            var err = BuildHttpResponse(502, "Bad Gateway", "Bad Gateway");
            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(err), ct);
        }
    }

    private async Task<(string Result, int? WebsiteId, string? Reason)> CheckAccessAsync(int childId, string domain)
    {
        if (childId == 0) return ("blocked", null, "Không xác định được danh tính người dùng.");

        try
        {
            var result = await _db.CheckWebAccessSpResults
                .FromSqlRaw("CALL sp_CheckWebAccess({0}, {1})", childId, domain)
                .ToListAsync(); // sp_CheckWebAccess returns status, id, reason

            var first = result.FirstOrDefault();
            if (first == null) return ("blocked", null, "Lỗi hệ thống kiểm tra truy cập.");

            return (first.AccessResult, first.AllowedWebsiteId, first.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "sp_CheckWebAccess failed");
            return ("blocked", null, "Lỗi server.");
        }
    }

    private async Task LogAccessAsync(int childId, string domain, string? url, string result, int? websiteId, int duration)
    {
        if (childId == 0) return;
        try
        {
            _db.WebAccessLogs.Add(new WebAccessLog
            {
                ChildId = childId,
                Domain = domain,
                FullUrl = url,
                AccessResult = result == "allowed" ? AccessResult.Allowed : AccessResult.Blocked,
                AllowedWebsiteId = websiteId,
                DurationSeconds = duration,
                SessionStart = DateTime.UtcNow,
                SessionEnd = duration > 0 ? DateTime.UtcNow : null
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log access");
        }
    }

    private async Task UpsertUsageAsync(int childId, int websiteId, string domain, int seconds)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("CALL sp_UpsertDailyUsage({0}, {1}, {2}, {3})",
                childId, websiteId, domain, seconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "sp_UpsertDailyUsage failed");
        }
    }

    private async Task<int> GetChildIdAsync(Dictionary<string, string> headers, string? ip)
    {
        // 1. Check Auth header
        if (headers.TryGetValue("proxy-authorization", out var auth))
        {
            var token = auth.Replace("Bearer ", "").Trim();
            try
            {
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(token);
                var sub = jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
                if (int.TryParse(sub, out var uid)) return uid;
            }
            catch { /* skip */ }
        }

        // 2. Check IP Mapping
        if (!string.IsNullOrEmpty(ip))
        {
            var mapping = await _db.ProxyIpMappings
                .Where(m => m.IpAddress == ip && m.IsActive)
                .Select(m => m.ChildId)
                .FirstOrDefaultAsync();
            if (mapping > 0) return mapping;
        }

        return 0;
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (await stream.ReadAsync(buf.AsMemory(0, 1), ct) > 0)
        {
            if (buf[0] == '\n') break;
            if (buf[0] != '\r') sb.Append((char)buf[0]);
        }
        return sb.ToString();
    }

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(line)) break;
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
                headers[line[..colonIdx].Trim().ToLowerInvariant()] = line[(colonIdx + 1)..].Trim();
        }
        return headers;
    }

    private static string BuildHttpResponse(int code, string status, string body)
    {
        var len = Encoding.UTF8.GetByteCount(body);
        return $"HTTP/1.1 {code} {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {len}\r\nConnection: close\r\n\r\n{body}";
    }
}
