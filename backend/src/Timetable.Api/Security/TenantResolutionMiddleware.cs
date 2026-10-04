using Microsoft.AspNetCore.Mvc;
using Timetable.Application.Abstractions;
using Timetable.Infrastructure.Identity;

namespace Timetable.Api.Security;

/// <summary>
/// Resolves the institution the request operates on (X-Institution-Id header, else the user's default, else the first
/// membership), validates membership, loads permissions and sets the tenant used by EF query filters.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string Header = "X-Institution-Id";

    public async Task InvokeAsync(HttpContext ctx, ICurrentUser currentUser, IPermissionService permissions, ITenantContext tenant, IMessageLocalizer localizer)
    {
        if (currentUser is CurrentUser cu && cu.IsAuthenticated && cu.UserId is { } userId)
        {
            var memberships = await permissions.GetMembershipsAsync(userId, ctx.RequestAborted);
            Guid? target = null;
            var header = ctx.Request.Headers[Header].ToString();
            if (string.IsNullOrEmpty(header)) header = ctx.Request.Query["institutionId"].ToString();
            if (Guid.TryParse(header, out var requested))
            {
                if (memberships.All(m => m.InstitutionId != requested))
                {
                    await WriteForbidden(ctx, localizer);
                    return;
                }
                target = requested;
            }
            if (target is null && Guid.TryParse(ctx.User.FindFirst(AppClaims.DefaultInstitution)?.Value, out var def) && memberships.Any(m => m.InstitutionId == def))
                target = def;
            target ??= memberships.FirstOrDefault()?.InstitutionId;
            if (target is { } inst)
            {
                cu.InstitutionId = inst;
                cu.Permissions = await permissions.GetPermissionsAsync(userId, inst, ctx.RequestAborted);
                tenant.Set(inst);
            }
        }
        await next(ctx);
    }

    private static Task WriteForbidden(HttpContext ctx, IMessageLocalizer localizer)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return ctx.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 403,
            Title = localizer.Localize("FORBIDDEN"),
            Extensions = { ["code"] = "FORBIDDEN", ["message"] = localizer.Localize("FORBIDDEN") },
        }, options: null, contentType: "application/problem+json");
    }
}
