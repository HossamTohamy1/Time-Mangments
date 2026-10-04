using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Timetable.Api.Hosting;

/// <summary>
/// Security headers for every response. The SPA's CSP allows only same-origin resources; the inline theme/language
/// bootstrap script in index.html is allowed by its SHA-256 hash (computed from the deployed file at start-up).
/// </summary>
public sealed partial class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _csp;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
    {
        _next = next;
        var hashes = InlineScriptHashes(Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "index.html"));
        var scripts = string.Join(' ', hashes.Select(h => $"'sha256-{h}'"));
        _csp = string.Join("; ",
            "default-src 'self'",
            $"script-src 'self' {scripts}".Trim(),
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["X-Frame-Options"] = "DENY";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        // Swagger UI (development only) ships its own inline scripts; everything else gets the strict policy.
        if (!ctx.Request.Path.StartsWithSegments("/api/swagger")) h["Content-Security-Policy"] = _csp;
        return _next(ctx);
    }

    public static IReadOnlyList<string> InlineScriptHashes(string indexPath)
    {
        if (!File.Exists(indexPath)) return [];
        var html = File.ReadAllText(indexPath);
        return InlineScript().Matches(html).Select(m => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))).ToList();
    }

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}
