using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

public sealed class ConcurrencyExceptionHandler(ILogger<ConcurrencyExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // A number that could not be claimed is a conflict, not a fault: the request was valid and resubmitting
        // it is the right response. It is answered with a code of its own so a caller can tell it apart from a
        // stale record, which needs a refresh first and is not worth retrying blind.
        if (exception is IdentifierAllocationException allocation)
        {
            logger.LogWarning(allocation, "Could not allocate a controlled number for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "The next controlled number could not be allocated. Submit this again.",
                code = "identifier_allocation_conflict"
            }, cancellationToken);
            return true;
        }
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
