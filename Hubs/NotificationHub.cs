using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FamilyGuardian.Api.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    // ✅ THÊM METHOD NÀY
    public async Task JoinGuardianGroup(string guardianId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"guardian_{guardianId}");
        _logger.LogInformation("Guardian {GuardianId} joined notification group", guardianId);
    }

    public async Task JoinChildGroup(string childId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"child_{childId}");
        _logger.LogInformation("Child {ChildId} joined notification group", childId);
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            _logger.LogInformation("User {UserId} connected to NotificationHub", userId);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
            _logger.LogInformation("User {UserId} disconnected from NotificationHub", userId);
        }
        await base.OnDisconnectedAsync(exception);
    }
}
