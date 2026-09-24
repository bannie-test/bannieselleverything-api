using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SaasEcommerce.Api.Common;

/// <summary>An expected failure with a client-facing message. Rendered as ProblemDetails with <see cref="StatusCode"/>.</summary>
public class ApiException(int statusCode, string message, IDictionary<string, object?>? extensions = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public IDictionary<string, object?>? Extensions { get; } = extensions;

    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, message);
    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
    public static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static ApiException Unauthorized(string message) => new(StatusCodes.Status401Unauthorized, message);
}

public static class DbExceptions
{
    /// <summary>True when the save failed on a unique index (PostgreSQL 23505).</summary>
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
