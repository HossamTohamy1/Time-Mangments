using Microsoft.AspNetCore.Localization;
using Microsoft.IdentityModel.JsonWebTokens;
using Timetable.Application.Abstractions;

namespace Timetable.Api.Hosting;

/// <summary>
/// Language resolution: the SPA's explicit UI language (X-Client-Language) → authenticated user's saved preference →
/// Accept-Language → default (en).
/// </summary>
public sealed class UserPreferenceCultureProvider : RequestCultureProvider
{
    public const string ClientLanguageHeader = "X-Client-Language";

    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var client = httpContext.Request.Headers[ClientLanguageHeader].ToString();
        if (client is "ar" or "en") return new ProviderCultureResult(client);
        var sub = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;
        var identity = httpContext.RequestServices.GetRequiredService<IIdentityService>();
        var lang = await identity.GetPreferredLanguageAsync(userId, httpContext.RequestAborted);
        return lang is "ar" or "en" ? new ProviderCultureResult(lang) : null;
    }
}
