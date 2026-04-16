using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Models.DTOs.WebsiteCheck;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class WebsiteCheckService : IWebsiteCheckService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<WebsiteCheckService> _logger;

    public WebsiteCheckService(AppDbContext db, IHttpClientFactory httpClientFactory,
        IConfiguration config, ILogger<WebsiteCheckService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<WebsiteCheckResult> CheckAsync(string domain, bool forceRefresh = false, CancellationToken ct = default)
    {
        domain = DomainNormalizer.Normalize(domain);
        
        if (!forceRefresh)
        {
            var cached = await _db.WebsiteCheckCaches
                .FirstOrDefaultAsync(c => c.Domain == domain, ct);

            if (cached != null && cached.ExpiresAt > DateTime.UtcNow)
            {
                return new WebsiteCheckResult
                {
                    Domain = domain,
                    IsReachable = cached.IsReachable ?? false,
                    HttpStatusCode = cached.HttpStatusCode,
                    ResponseTimeMs = cached.ResponseTimeMs,
                    IsSafe = cached.IsSafe ?? true,
                    ThreatType = cached.ThreatType,
                    FaviconUrl = cached.FaviconUrl,
                    DisplayName = cached.DisplayName,
                    CheckedAt = cached.CheckedAt
                };
            }
        }

        // Perform Check
        var result = new WebsiteCheckResult
        {
            Domain = domain,
            CheckedAt = DateTime.UtcNow
        };

        var client = _httpClientFactory.CreateClient("WebCheck");
        var sw = Stopwatch.StartNew();

        try
        {
            // Try HEAD then GET
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"https://{domain}"), ct);
            if (!response.IsSuccessStatusCode)
            {
                response = await client.GetAsync($"https://{domain}", ct);
            }

            sw.Stop();
            result.IsReachable = response.IsSuccessStatusCode || (int)response.StatusCode < 500;
            result.HttpStatusCode = (int)response.StatusCode;
            result.ResponseTimeMs = (int)sw.ElapsedMilliseconds;

            // Try to get Display Name (Title) if GET success
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html")
            {
                var html = await response.Content.ReadAsStringAsync(ct);
                var match = System.Text.RegularExpressions.Regex.Match(html, @"<title>\s*(.+?)\s*</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    result.DisplayName = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP check failed for {Domain}", domain);
            result.IsReachable = false;
            sw.Stop();
            result.ResponseTimeMs = (int)sw.ElapsedMilliseconds;
        }

        result.FaviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=64";

        // Google Safe Browsing
        var (isSafe, threatType) = await CheckSafeBrowsingAsync(domain, ct);
        result.IsSafe = isSafe;
        result.ThreatType = threatType;

        // Save/Update Cache
        var cache = await _db.WebsiteCheckCaches.FindAsync(new object[] { domain }, ct);
        var expiresAt = DateTime.UtcNow.AddMinutes(_config.GetValue("WebsiteCheck:CacheMinutes", 30));

        if (cache == null)
        {
            cache = new WebsiteCheckCache { Domain = domain };
            _db.WebsiteCheckCaches.Add(cache);
        }

        cache.IsReachable = result.IsReachable;
        cache.HttpStatusCode = result.HttpStatusCode;
        cache.ResponseTimeMs = result.ResponseTimeMs;
        cache.IsSafe = result.IsSafe;
        cache.ThreatType = result.ThreatType;
        cache.FaviconUrl = result.FaviconUrl;
        cache.DisplayName = result.DisplayName;
        cache.CheckedAt = result.CheckedAt;
        cache.ExpiresAt = expiresAt;

        await _db.SaveChangesAsync(ct);

        return result;
    }

    private async Task<(bool IsSafe, string? ThreatType)> CheckSafeBrowsingAsync(string domain, CancellationToken ct)
    {
        var apiKey = _config["Google:SafeBrowsingApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return (true, null);

        try
        {
            var client = _httpClientFactory.CreateClient("WebCheck");
            var body = JsonSerializer.Serialize(new
            {
                client = new { clientId = "familyguardian", clientVersion = "1.0" },
                threatInfo = new
                {
                    threatTypes = new[] { "MALWARE", "SOCIAL_ENGINEERING", "UNWANTED_SOFTWARE", "POTENTIALLY_HARMFUL_APPLICATION" },
                    platformTypes = new[] { "ANY_PLATFORM" },
                    threatEntryTypes = new[] { "URL" },
                    threatEntries = new[] { new { url = $"https://{domain}" } }
                }
            });

            var response = await client.PostAsync(
                $"https://safebrowsing.googleapis.com/v4/threatMatches:find?key={apiKey}",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);

            if (!response.IsSuccessStatusCode) return (true, null);

            var content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("matches", out var matches) && matches.GetArrayLength() > 0)
            {
                var threatType = matches[0].GetProperty("threatType").GetString();
                return (false, threatType);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Safe Browsing check failed for {Domain}", domain);
            return (true, null);
        }
    }
}
