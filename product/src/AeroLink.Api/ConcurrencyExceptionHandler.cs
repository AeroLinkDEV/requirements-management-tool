using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

public sealed class ConcurrencyExceptionHandler(ILogger<ConcurrencyExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
        {
            logger.LogError(exception, "Unhandled API failure for {Method} {Path}", context.Request.Method, context.Request.Path);
            return false;
        }
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "This record changed after it was loaded. Refresh it and reapply your change.",
            code = "concurrency_conflict"
        }, cancellationToken);
        return true;
    }
}
