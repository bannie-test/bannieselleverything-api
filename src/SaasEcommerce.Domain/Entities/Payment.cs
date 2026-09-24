using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

public class Payment : TenantEntityBase, IAuditable
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public PaymentGatewayType Gateway { get; set; }

    /// <summary>
    /// Our reference sent to the gateway (ZaloPay app_trans_id, BIDV vpc_MerchTxnRef).
    /// Globally unique. Callbacks find the payment, and through it the tenant, by this value.
    /// </summary>
    public string MerchantReference { get; set; } = "";

    /// <summary>The gateway's own transaction id (ZaloPay zp_trans_id, BIDV vpc_TransactionNo). Set on callback.</summary>
    public string? GatewayTransactionId { get; set; }

    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "VND";
    public PaymentStatus Status { get; set; } = PaymentStatus.Initiated;
    public string IdempotencyKey { get; set; } = "";
    public string? RawRequest { get; set; }
    public string? RawResponse { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>Maps to PostgreSQL xmin.</summary>
    public uint Version { get; set; }
}
