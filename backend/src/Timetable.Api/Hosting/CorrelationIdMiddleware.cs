using Serilog.Context;

namespace Timetable.Api.Hosting;

/// <summary>Propagates (or creates) an X-Correlation-ID header and pushes it into the log context.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers.TryGetValue(HeaderName, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : context.TraceIdentifier;
        if (id.Length > 64) id = id[..64];
        context.TraceIdentifier = id;
        context.Response.Headers[HeaderName] = id;
        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
