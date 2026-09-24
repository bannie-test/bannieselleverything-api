namespace SaasEcommerce.Domain.Enums;

public enum TenantStatus { Trial, Active, Suspended }

public enum PlanTier { Free, Basic, Pro }

public enum UserRole { PlatformAdmin, ShopOwner, ShopStaff }

public enum OrderStatus { Pending, AwaitingPayment, Paid, Processing, Shipped, Delivered, Cancelled, Refunded }

/// <summary>
/// Stock is reserved (decremented) at checkout, committed when payment succeeds,
/// and released (re-incremented) when payment fails or times out.
/// </summary>
public enum StockReservationStatus { Reserved, Committed, Released }

public enum PaymentGatewayType { ZaloPay, Bidv }

public enum PaymentStatus { Initiated, Pending, Succeeded, Failed, Refunded }

/// <summary>Which authentication realm a token or actor belongs to. Maps 1:1 to the JWT audience.</summary>
public enum AuthRealm { Storefront, Admin, Platform }

public enum AuditAction { Created, Updated, Deleted }

public enum ActorType { System, User, Customer, Gateway }
