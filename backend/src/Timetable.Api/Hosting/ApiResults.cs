using Microsoft.AspNetCore.Mvc;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Behaviors;
using Timetable.Domain.Common;
using Timetable.Infrastructure.Localization;

namespace Timetable.Api.Hosting;

/// <summary>
/// Maps <see cref="Result"/> failures to RFC 7807 problem details with a stable code, a localized message and
/// structured (rendered) params, so the client can show the server message or translate the code itself.
/// </summary>
public static class ApiResults
{
    public static ProblemDetails ToProblem(Error error, HttpContext ctx)
    {
        var localizer = ctx.RequestServices.GetRequiredService<IMessageLocalizer>();
        var lang = ctx.RequestServices.GetRequiredService<ICurrentUser>().Language;
        var status = error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict or ErrorKind.Concurrency => StatusCodes.Status409Conflict,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest,
        };
        var message = localizer.Localize(error.Code, error.Params, lang);
        var problem = new ProblemDetails
        {
            Status = status,
            Title = message,
            Type = $"https://timetable.local/errors/{error.Code.ToLowerInvariant()}",
        };
        problem.Extensions["code"] = error.Code;
        problem.Extensions["message"] = message;
        problem.Extensions["traceId"] = ctx.TraceIdentifier;
        if (error.Params is { Count: > 0 })
            problem.Extensions["params"] = error.Params.ToDictionary(kv => kv.Key, kv => JsonMessageLocalizer.Render(kv.Value, lang));
        if (error.Details is IDictionary<string, FieldError[]> fields)
        {
            problem.Extensions["errors"] = fields.ToDictionary(kv => kv.Key, kv => kv.Value.Select(f => new
            {
                code = f.Code,
                message = localizer.Localize(f.Code, f.Params?.ToDictionary(p => p.Key, p => (object?)p.Value), lang) is var m && m != f.Code ? m : f.Message,
                @params = f.Params,
            }).ToArray());
        }
        else if (error.Details is not null)
        {
            problem.Extensions["details"] = error.Details;
        }
        return problem;
    }

    public static IActionResult ToProblemResult(this ControllerBase c, Error error)
    {
        var p = ToProblem(error, c.HttpContext);
        return new ObjectResult(p) { StatusCode = p.Status, ContentTypes = { "application/problem+json" } };
    }

    public static IActionResult ToActionResult<T>(this ControllerBase c, Result<T> r) => r.IsSuccess ? c.Ok(r.Value) : c.ToProblemResult(r.Error!);

    public static IActionResult ToActionResult(this ControllerBase c, Result r) => r.IsSuccess ? c.NoContent() : c.ToProblemResult(r.Error!);

    public static IActionResult ToCreated<T>(this ControllerBase c, Result<T> r, Func<T, object> id) =>
        r.IsSuccess ? c.StatusCode(StatusCodes.Status201Created, r.Value) : c.ToProblemResult(r.Error!);
}
