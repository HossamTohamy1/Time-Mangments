using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Timetable.Api.Hosting;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;

namespace Timetable.Api.Security;

/// <summary>Rejects the request with FEATURE_DISABLED (403) when the institution has the feature switched off.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFeatureAttribute(string feature) : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var features = context.HttpContext.RequestServices.GetRequiredService<IFeatureService>();
        if (!await features.IsEnabledAsync(feature, context.HttpContext.RequestAborted))
        {
            var p = ApiResults.ToProblem(Error.Forbidden("FEATURE_DISABLED"), context.HttpContext);
            context.Result = new ObjectResult(p) { StatusCode = p.Status };
            return;
        }
        await next();
    }
}
