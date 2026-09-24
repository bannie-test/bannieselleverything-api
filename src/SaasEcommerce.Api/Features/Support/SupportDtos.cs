using System.Security.Cryptography;
using FluentValidation;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Api.Features.Support;

public record TicketSummaryDto(string Number, string Subject, string? OrderNumber, SupportTicketStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt, SupportAuthorType LastAuthor);

public record SupportMessageDto(Guid Id, SupportAuthorType AuthorType, string AuthorName, string Body, DateTimeOffset CreatedAt);

public record TicketDto(string Number, string Subject, string? OrderNumber, SupportTicketStatus Status, DateTimeOffset CreatedAt,
    List<SupportMessageDto> Messages);

public record AdminTicketDto(TicketDto Ticket, string CustomerName, string CustomerEmail, string? CustomerPhone);

public record AdminTicketSummaryDto(TicketSummaryDto Ticket, string CustomerName, string CustomerEmail);

public record CreateTicketRequest(string Subject, string Message, string? OrderNumber);

public record PostMessageRequest(string Body);

public record ChangeTicketStatusRequest(SupportTicketStatus Status);

public record AdminTicketQuery(SupportTicketStatus? Status, string? Q, int Page = 1, int PageSize = 20);

public class CreateTicketValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.OrderNumber).MaximumLength(32);
    }
}

public class PostMessageValidator : AbstractValidator<PostMessageRequest>
{
    public PostMessageValidator() => RuleFor(x => x.Body).NotEmpty().MaximumLength(4000);
}

public static class SupportMapping
{
    private const string NumberAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>"T260924-7KQ2M": date plus 5 unambiguous random characters. Unique per tenant by index.</summary>
    public static string NewTicketNumber(DateTimeOffset now) => $"T{now:yyMMdd}-{RandomNumberGenerator.GetString(NumberAlphabet, 5)}";

    public static IQueryable<TicketSummaryDto> ToSummaries(this IQueryable<SupportTicket> tickets) => tickets.Select(t => new TicketSummaryDto(
        t.Number, t.Subject, t.OrderNumber, t.Status, t.CreatedAt, t.LastMessageAt,
        t.Messages.OrderByDescending(m => m.CreatedAt).Select(m => m.AuthorType).FirstOrDefault()));

    public static TicketDto ToDto(this SupportTicket t) => new(
        t.Number, t.Subject, t.OrderNumber, t.Status, t.CreatedAt,
        t.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new SupportMessageDto(m.Id, m.AuthorType, m.AuthorName, m.Body, m.CreatedAt)).ToList());
}
