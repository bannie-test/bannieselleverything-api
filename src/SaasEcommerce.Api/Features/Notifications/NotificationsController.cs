using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Notifications;

public record NotificationDto(Guid Id, NotificationType Type, string Title, string Body, string? Link, DateTimeOffset CreatedAt, bool IsRead);

public record UnreadCountDto(int Count);

[ApiController]
[Route("api/storefront/account/notifications")]
[RequireTenant]
[Authorize(Policies.Customer)]
public class NotificationsController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<NotificationDto>> List([FromQuery] int page = 1, CancellationToken ct = default)
    {
        const int pageSize = 20;
        page = Math.Max(1, page);
        var query = Mine().AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt != null))
            .ToListAsync(ct);
        return new PagedResult<NotificationDto>(items, page, pageSize, total);
    }

    [HttpGet("unread-count")]
    public async Task<UnreadCountDto> UnreadCount(CancellationToken ct) =>
        new(await Mine().CountAsync(n => n.ReadAt == null, ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var updated = await Mine().Where(n => n.Id == id && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now).SetProperty(n => n.UpdatedAt, now), ct);
        return updated == 0 && !await Mine().AnyAsync(n => n.Id == id, ct) ? NotFound() : NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await Mine().Where(n => n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now).SetProperty(n => n.UpdatedAt, now), ct);
        return NoContent();
    }

    private IQueryable<Domain.Entities.Notification> Mine() => db.Notifications.Where(n => n.CustomerId == User.CustomerId());
}
