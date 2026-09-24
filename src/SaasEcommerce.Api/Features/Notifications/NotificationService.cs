using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Notifications;

/// <summary>
/// Queues in-app notifications on the current DbContext. They are saved by the caller's
/// SaveChanges, so a notification exists only if the change it describes was committed.
/// </summary>
public class NotificationService(AppDbContext db)
{
    public void Notify(Guid customerId, NotificationType type, string title, string body, string? link = null) =>
        db.Notifications.Add(new Notification { CustomerId = customerId, Type = type, Title = title, Body = body, Link = link });
}
