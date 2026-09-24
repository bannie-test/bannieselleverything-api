using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Application.Common.Tenancy;

namespace SaasEcommerce.Api.Common;

/// <summary>Maps known exceptions to ProblemDetails. Anything else is a 500 with no internal detail.</summary>
public class ExceptionHandler(IProblemDetailsService problemDetails, ILogger<ExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ApiException api => (api.StatusCode, api.Message),
            TenantNotResolvedException => (StatusCodes.Status400BadRequest, "No shop could be resolved for this request."),
            CrossTenantWriteException => (StatusCodes.Status403Forbidden, "Forbidden."),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The record was changed by someone else. Reload and try again."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception");
        else if (exception is CrossTenantWriteException)
            logger.LogWarning(exception, "Blocked cross-tenant write");

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        if (exception is ApiException { Extensions: { } extensions })
        {
            foreach (var (key, value) in extensions)
                problem.Extensions[key] = value;
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
