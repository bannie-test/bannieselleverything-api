namespace SaasEcommerce.Application.Common.Tenancy;

/// <summary>Thrown when an operation needs a tenant and none was resolved. Maps to HTTP 400.</summary>
public class TenantNotResolvedException(string message = "No tenant could be resolved for this request.")
    : Exception(message);

/// <summary>Thrown when code tries to write a row belonging to a different tenant than the current one.</summary>
public class CrossTenantWriteException(string message) : Exception(message);
