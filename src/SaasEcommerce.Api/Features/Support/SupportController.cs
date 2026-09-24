using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Support;

/// <summary>Customers open support tickets with the shop, optionally about one of their orders, and follow the conversation.</summary>
[ApiController]
[Route("api/storefront/account/support")]
[RequireTenant]
[Authorize(Policies.Customer)]
public class SupportController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<TicketSummaryDto>> List([FromQuery] int page = 1, CancellationToken ct = default)
    {
        const int pageSize = 20;
        page = Math.Max(1, page);
        var query = Mine().AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(t => t.LastMessageAt).Skip((page - 1) * pageSize).Take(pageSize).ToSummaries().ToListAsync(ct);
        return new PagedResult<TicketSummaryDto>(items, page, pageSize, total);
    }

    [HttpPost]
    public async Task<ActionResult<TicketDto>> Create(CreateTicketRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == User.CustomerId(), ct);
        var orderNumber = string.IsNullOrWhiteSpace(request.OrderNumber) ? null : request.OrderNumber.Trim().ToUpperInvariant();
        if (orderNumber is not null && !await db.Orders.AnyAsync(o => o.OrderNumber == orderNumber && o.CustomerId == customer.Id, ct))
            throw ApiException.BadRequest("That order wasn't found in your account.");

        var now = clock.GetUtcNow();
        var ticket = new SupportTicket
        {
            Number = SupportMapping.NewTicketNumber(now),
            CustomerId = customer.Id,
            Subject = request.Subject.Trim(),
            OrderNumber = orderNumber,
            LastMessageAt = now,
        };
        ticket.Messages.Add(new SupportMessage { AuthorType = SupportAuthorType.Customer, AuthorName = customer.FullName, Body = request.Message.Trim() });
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, ticket.ToDto());
    }

    [HttpGet("{number}")]
    public async Task<ActionResult<TicketDto>> Get(string number, CancellationToken ct)
    {
        var ticket = await Mine().AsNoTracking().Include(t => t.Messages).FirstOrDefaultAsync(t => t.Number == number, ct);
        return ticket is null ? NotFound() : ticket.ToDto();
    }

    /// <summary>Replying to a closed ticket reopens it.</summary>
    [HttpPost("{number}/messages")]
    public async Task<ActionResult<TicketDto>> Reply(string number, PostMessageRequest request, CancellationToken ct)
    {
        var ticket = await Mine().Include(t => t.Messages).FirstOrDefaultAsync(t => t.Number == number, ct);
        if (ticket is null)
            return NotFound();
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == ticket.CustomerId, ct);

        var message = new SupportMessage
        {
            TicketId = ticket.Id, AuthorType = SupportAuthorType.Customer, AuthorName = customer.FullName, Body = request.Body.Trim(),
        };
        ticket.Messages.Add(message);
        db.SupportMessages.Add(message);
        ticket.Status = SupportTicketStatus.Open;
        ticket.LastMessageAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ticket.ToDto();
    }

    [HttpPost("{number}/close")]
    public async Task<ActionResult<TicketDto>> Close(string number, CancellationToken ct)
    {
        var ticket = await Mine().Include(t => t.Messages).FirstOrDefaultAsync(t => t.Number == number, ct);
        if (ticket is null)
            return NotFound();
        ticket.Status = SupportTicketStatus.Closed;
        await db.SaveChangesAsync(ct);
        return ticket.ToDto();
    }

    private IQueryable<SupportTicket> Mine() => db.SupportTickets.Where(t => t.CustomerId == User.CustomerId());
}
