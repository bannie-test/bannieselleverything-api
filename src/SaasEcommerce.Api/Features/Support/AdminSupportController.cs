using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Features.Notifications;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Support;

/// <summary>The shop's support inbox. A staff reply marks the ticket Answered and notifies the customer.</summary>
[ApiController]
[Route("api/admin/support")]
[Authorize(Policies.ShopAdmin)]
public class AdminSupportController(AppDbContext db, NotificationService notifications, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AdminTicketSummaryDto>> List([FromQuery] AdminTicketQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var q = db.SupportTickets.AsNoTracking();
        if (query.Status is { } status)
            q = q.Where(t => t.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{query.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            q = q.Where(t => EF.Functions.ILike(t.Number, pattern) || EF.Functions.ILike(t.Subject, pattern)
                || (t.OrderNumber != null && EF.Functions.ILike(t.OrderNumber, pattern)) || EF.Functions.ILike(t.Customer!.Email, pattern));
        }

        var total = await q.CountAsync(ct);
        var tickets = q.OrderByDescending(t => t.LastMessageAt).Skip((page - 1) * pageSize).Take(pageSize);
        var items = await tickets.Select(t => new AdminTicketSummaryDto(
                new TicketSummaryDto(t.Number, t.Subject, t.OrderNumber, t.Status, t.CreatedAt, t.LastMessageAt,
                    t.Messages.OrderByDescending(m => m.CreatedAt).Select(m => m.AuthorType).FirstOrDefault()),
                t.Customer!.FullName, t.Customer.Email))
            .ToListAsync(ct);
        return new PagedResult<AdminTicketSummaryDto>(items, page, pageSize, total);
    }

    [HttpGet("{number}")]
    public async Task<ActionResult<AdminTicketDto>> Get(string number, CancellationToken ct)
    {
        var ticket = await Query().AsNoTracking().FirstOrDefaultAsync(t => t.Number == number, ct);
        return ticket is null ? NotFound() : ToDto(ticket);
    }

    [HttpPost("{number}/messages")]
    public async Task<ActionResult<AdminTicketDto>> Reply(string number, PostMessageRequest request, CancellationToken ct)
    {
        var ticket = await Query().FirstOrDefaultAsync(t => t.Number == number, ct);
        if (ticket is null)
            return NotFound();

        var staffName = await db.Users.Where(u => u.Id == User.SubjectId()).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        var message = new SupportMessage
        {
            TicketId = ticket.Id, AuthorType = SupportAuthorType.Staff, AuthorName = staffName ?? "Shop staff", Body = request.Body.Trim(),
        };
        ticket.Messages.Add(message);
        db.SupportMessages.Add(message);
        ticket.Status = SupportTicketStatus.Answered;
        ticket.LastMessageAt = clock.GetUtcNow();
        notifications.Notify(ticket.CustomerId, NotificationType.SupportReply, $"New reply on ticket {ticket.Number}",
            Excerpt(message.Body), $"/account/support/{ticket.Number}");

        await db.SaveChangesAsync(ct);
        return ToDto(ticket);
    }

    [HttpPost("{number}/status")]
    public async Task<ActionResult<AdminTicketDto>> ChangeStatus(string number, ChangeTicketStatusRequest request, CancellationToken ct)
    {
        var ticket = await Query().FirstOrDefaultAsync(t => t.Number == number, ct);
        if (ticket is null)
            return NotFound();
        ticket.Status = request.Status;
        await db.SaveChangesAsync(ct);
        return ToDto(ticket);
    }

    private IQueryable<SupportTicket> Query() => db.SupportTickets.Include(t => t.Messages).Include(t => t.Customer);

    private static AdminTicketDto ToDto(SupportTicket t) => new(t.ToDto(), t.Customer!.FullName, t.Customer.Email, t.Customer.Phone);

    private static string Excerpt(string body) => body.Length <= 140 ? body : body[..137] + "…";
}
