using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Timetable.Api.Hosting;

/// <summary>Serves the Angular build from wwwroot with correct cache headers and a SPA fallback that never swallows /api or /hubs.</summary>
public static class SpaHostingExtensions
{
    public static WebApplication UseSpaStaticFiles(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var headers = ctx.Context.Response.Headers;
                var name = ctx.File.Name;
                if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
                }
                else if (IsHashed(name))
                {
                    headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
                }
                else
                {
                    headers[HeaderNames.CacheControl] = "public, max-age=3600";
                }
            },
        });
        return app;
    }

    /// <summary>Angular emits names like main-ABCD1234.js / styles-XYZ.css.</summary>
    internal static bool IsHashed(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        if (dash < 0 || dash == stem.Length - 1) return false;
        var hash = stem[(dash + 1)..];
        return hash.Length >= 8 && hash.All(char.IsLetterOrDigit) && hash.Any(c => char.IsUpper(c) || char.IsDigit(c));
    }

    public static WebApplication MapSpaFallback(this WebApplication app)
    {
        // Unknown API / hub routes return RFC 7807 JSON, never index.html.
        app.Map("/api/{**rest}", NotFoundProblem);
        app.Map("/hubs/{**rest}", NotFoundProblem);

        var indexExists = app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists;
        if (indexExists)
        {
            app.MapFallbackToFile("index.html", new StaticFileOptions
            {
                OnPrepareResponse = ctx => ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate",
            });
        }
        else
        {
            app.MapFallback(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain";
                return ctx.Response.WriteAsync("Client not built. Run `npm run build:prod` in /frontend (or build.sh).");
            });
        }
        return app;
    }

    private static IResult NotFoundProblem(HttpContext ctx) =>
        Results.Problem(new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Not Found",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.5",
            Detail = $"No API endpoint matches '{ctx.Request.Path}'.",
            Extensions = { ["code"] = "NOT_FOUND", ["traceId"] = ctx.TraceIdentifier },
        });

    public static IFileProvider? WebRoot(this IWebHostEnvironment env) => env.WebRootFileProvider;
}
