using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;

namespace Timetable.Api.Hosting;

/// <summary>Unexpected exceptions → RFC 7807 (500) without leaking internals; concurrency → 409.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IMessageLocalizer localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested) return true;
        ProblemDetails problem;
        if (exception is DbUpdateConcurrencyException)
        {
            problem = ApiResults.ToProblem(Error.Concurrency(), ctx);
        }
        else if (exception is DbUpdateException dbe && IsUniqueViolation(dbe))
        {
            problem = ApiResults.ToProblem(Error.Conflict("MOVE_CONFLICT"), ctx);
        }
        else
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            var message = localizer.Localize("UNEXPECTED_ERROR");
            problem = new ProblemDetails { Status = 500, Title = message, Extensions = { ["code"] = "UNEXPECTED_ERROR", ["message"] = message, ["traceId"] = ctx.TraceIdentifier } };
        }
        ctx.Response.StatusCode = problem.Status ?? 500;
        await ctx.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || e.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
}
