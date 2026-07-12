using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

public sealed class ConcurrencyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException) return false;
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "This record changed after it was loaded. Refresh it and reapply your change.",
            code = "concurrency_conflict"
        }, cancellationToken);
        return true;
    }
}
