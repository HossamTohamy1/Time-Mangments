using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Timetable.Api.Hosting;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;

namespace Timetable.Api.Controllers;

public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt);
public sealed record MeResponse(UserProfileDto Profile, IReadOnlyList<InstitutionMembership> Institutions, Guid? InstitutionId, IReadOnlyCollection<string> Permissions);

[Route("api/v{version:apiVersion}/auth")]
public sealed class AuthController(IIdentityService identity, ICurrentUser currentUser, IPermissionService permissions, IWebHostEnvironment env) : ApiControllerBase
{
    public const string RefreshCookie = "tt_refresh";

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(200)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return this.ToProblemResult(Error.Validation("INVALID_CREDENTIALS"));
        var result = await identity.LoginAsync(request.Email, request.Password, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (result.IsFailure) return this.ToProblemResult(result.Error!);
        SetRefreshCookie(result.Value);
        return Ok(new AuthResponse(result.Value.AccessToken, result.Value.AccessTokenExpiresAt));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType<AuthResponse>(200)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookie, out var token) || string.IsNullOrEmpty(token))
            return this.ToProblemResult(Error.Validation("INVALID_REFRESH_TOKEN"));
        var result = await identity.RefreshAsync(token, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (result.IsFailure)
        {
            Response.Cookies.Delete(RefreshCookie, CookieOptions(DateTimeOffset.UnixEpoch));
            return this.ToProblemResult(result.Error!);
        }
        SetRefreshCookie(result.Value);
        return Ok(new AuthResponse(result.Value.AccessToken, result.Value.AccessTokenExpiresAt));
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(RefreshCookie, out var token) && !string.IsNullOrEmpty(token))
            await identity.RevokeAsync(token, ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(DateTimeOffset.UnixEpoch));
        return NoContent();
    }

    [HttpGet("me")]
    [ProducesResponseType<MeResponse>(200)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = currentUser.UserId!.Value;
        var profile = await identity.GetProfileAsync(userId, ct);
        if (profile is null) return this.ToProblemResult(Error.NotFound("user", userId));
        var memberships = await permissions.GetMembershipsAsync(userId, ct);
        Guid? inst = currentUser.InstitutionId == Guid.Empty ? null : currentUser.InstitutionId;
        return Ok(new MeResponse(profile, memberships, inst, currentUser.Permissions));
    }

    [HttpPut("profile")]
    [ProducesResponseType<UserProfileDto>(200)]
    public async Task<IActionResult> UpdateProfile(ProfileUpdate update, CancellationToken ct) =>
        this.ToActionResult(await identity.UpdateProfileAsync(currentUser.UserId!.Value, update, ct));

    [HttpPost("change-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        this.ToActionResult(await identity.ChangePasswordAsync(currentUser.UserId!.Value, request.CurrentPassword, request.NewPassword, ct));

    private void SetRefreshCookie(AuthTokens tokens) =>
        Response.Cookies.Append(RefreshCookie, tokens.RefreshToken, CookieOptions(tokens.RefreshTokenExpiresAt));

    private CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = !env.IsDevelopment() || Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/api/v1/auth",
        Expires = expires,
        IsEssential = true,
    };
}
