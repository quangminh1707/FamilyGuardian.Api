using FamilyGuardian.Api.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace FamilyGuardian.Api.Jobs;

[DisallowConcurrentExecution]
public class StorageCleanupJob : IJob
{
    private readonly ILogger<StorageCleanupJob> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _env;

    private const int RetentionDays = 7;

    public StorageCleanupJob(
        ILogger<StorageCleanupJob> logger,
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _env = env;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("[StorageCleanup] Bắt đầu dọn dẹp ảnh cũ hơn {Days} ngày", RetentionDays);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        var oldScreenshots = await db.WebsiteScreenshots
            .Where(s => s.CapturedAt < cutoff && s.Status == "captured")
            .ToListAsync();

        if (oldScreenshots.Count == 0)
        {
            _logger.LogInformation("[StorageCleanup] Không có ảnh nào cần xóa.");
            return;
        }

        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var webRootFullPath = Path.GetFullPath(webRoot);

        var deletedFiles = 0;
        var deletedRecords = 0;

        foreach (var shot in oldScreenshots)
        {
            if (!string.IsNullOrWhiteSpace(shot.ImagePath))
            {
                var relativePath = shot.ImagePath.TrimStart('/', '\\');
                var filePath = Path.GetFullPath(Path.Combine(webRootFullPath, relativePath));

                if (filePath.StartsWith(webRootFullPath, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        deletedFiles++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[StorageCleanup] Không xóa được file: {Path}", filePath);
                    }
                }
            }

            db.WebsiteScreenshots.Remove(shot);
            deletedRecords++;
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "[StorageCleanup] Hoàn thành: xóa {Files} file, {Records} bản ghi DB.",
            deletedFiles, deletedRecords);
    }
}
