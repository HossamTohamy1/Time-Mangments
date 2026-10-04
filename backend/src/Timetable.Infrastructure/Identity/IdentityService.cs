using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Infrastructure.Persistence;

namespace Timetable.Infrastructure.Identity;

public sealed class IdentityService(
    UserManager<AppUser> users,
    AppDbContext db,
    IOptions<JwtOptions> jwt,
    IMemoryCache cache,
    TimeProvider clock) : IIdentityService
{
    private readonly JwtOptions _jwt = jwt.Value;

    public async Task<Result<AuthTokens>> LoginAsync(string email, string password, string? ip, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive) return Error.Validation("INVALID_CREDENTIALS");
        if (await users.IsLockedOutAsync(user)) return Error.Validation("ACCOUNT_LOCKED");
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return Error.Validation("INVALID_CREDENTIALS");
        }
        await users.ResetAccessFailedCountAsync(user);
        return await IssueAsync(user, ip, null, ct);
    }

    public async Task<Result<AuthTokens>> RefreshAsync(string refreshToken, string? ip, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (token is null) return Error.Validation("INVALID_REFRESH_TOKEN");
        if (!token.IsActive(now))
        {
            // Reuse of a rotated token: revoke the whole family for this user (token theft protection).
            if (token.RevokedAt is not null && token.ReplacedByHash is not null)
            {
                var active = await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null).ToListAsync(ct);
                foreach (var t in active) t.RevokedAt = now;
                await db.SaveChangesAsync(ct);
            }
            return Error.Validation("INVALID_REFRESH_TOKEN");
        }
        var user = await users.FindByIdAsync(token.UserId.ToString());
        if (user is null || !user.IsActive) return Error.Validation("INVALID_REFRESH_TOKEN");
        return await IssueAsync(user, ip, token, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.RevokedAt is not null) return;
        token.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string current, string next, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return Error.NotFound("user", userId);
        var r = await users.ChangePasswordAsync(user, current, next);
        if (!r.Succeeded)
            return Error.Validation("PASSWORD_CHANGE_FAILED", details: r.Errors.Select(e => e.Code).ToArray());
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var u = await users.FindByIdAsync(userId.ToString());
        return u is null ? null : ToProfile(u);
    }

    public async Task<Result<UserProfileDto>> UpdateProfileAsync(Guid userId, ProfileUpdate update, CancellationToken ct)
    {
        var u = await users.FindByIdAsync(userId.ToString());
        if (u is null) return Error.NotFound("user", userId);
        if (update.PreferredLanguage is "en" or "ar") u.PreferredLanguage = update.PreferredLanguage;
        if (update.PreferredTheme is "light" or "dark" or "system") u.PreferredTheme = update.PreferredTheme;
        if (update.DigitStyle is "western" or "arabic-indic") u.DigitStyle = update.DigitStyle;
        if (update.DisplayNameAr is not null) u.DisplayNameAr = update.DisplayNameAr;
        if (update.DisplayNameEn is not null) u.DisplayNameEn = update.DisplayNameEn;
        await users.UpdateAsync(u);
        cache.Remove(LangKey(userId));
        return ToProfile(u);
    }

    public async Task<string?> GetPreferredLanguageAsync(Guid userId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(LangKey(userId), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await db.Users.Where(u => u.Id == userId).Select(u => u.PreferredLanguage).FirstOrDefaultAsync(ct);
        });

    public async Task<IReadOnlyList<Guid>> LinkedUsersAsync(IReadOnlyCollection<Guid> instructorIds, IReadOnlyCollection<Guid> groupIds, CancellationToken ct) =>
        await db.Users.Where(u => u.IsActive && ((u.InstructorId != null && instructorIds.Contains(u.InstructorId.Value))
                || (u.StudentGroupId != null && groupIds.Contains(u.StudentGroupId.Value))))
            .Select(u => u.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<UserListItemDto>> ListUsersAsync(Guid institutionId, string? search, CancellationToken ct)
    {
        var assignments = await db.UserRoleAssignments.IgnoreQueryFilters().Where(a => a.InstitutionId == institutionId)
            .Join(db.AppRoles.IgnoreQueryFilters(), a => a.RoleId, r => r.Id, (a, r) => new { a.Id, a.UserId, a.RoleId, r.Code, a.OrgUnitId })
            .ToListAsync(ct);
        var ids = assignments.Select(a => a.UserId).Distinct().ToList();
        var q = db.Users.Where(u => ids.Contains(u.Id));
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(u => u.Email!.Contains(search) || (u.DisplayNameEn != null && u.DisplayNameEn.Contains(search)) || (u.DisplayNameAr != null && u.DisplayNameAr.Contains(search)));
        var list = await q.OrderBy(u => u.Email).ToListAsync(ct);
        return list.Select(u => new UserListItemDto(u.Id, u.Email!, u.DisplayNameAr, u.DisplayNameEn, u.IsActive, u.InstructorId, u.StudentGroupId,
            assignments.Where(a => a.UserId == u.Id).Select(a => new UserRoleDto(a.Id, a.RoleId, a.Code, a.OrgUnitId)).ToList())).ToList();
    }

    public async Task<Result<Guid>> UpsertUserAsync(Guid institutionId, Guid? userId, UpsertUserInput input, CancellationToken ct)
    {
        AppUser? user;
        if (userId is { } id)
        {
            user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Error.NotFound("user", id);
        }
        else
        {
            if (await users.FindByEmailAsync(input.Email) is not null) return Error.Conflict("EMAIL_ALREADY_EXISTS");
            if (string.IsNullOrWhiteSpace(input.Password)) return Error.Validation("PASSWORD_REQUIRED");
            user = new AppUser { UserName = input.Email, Email = input.Email, EmailConfirmed = true, CreatedAt = clock.GetUtcNow(), DefaultInstitutionId = institutionId };
        }
        user.DisplayNameAr = input.DisplayNameAr;
        user.DisplayNameEn = input.DisplayNameEn;
        user.IsActive = input.IsActive;
        user.InstructorId = input.InstructorId;
        user.StudentGroupId = input.StudentGroupId;
        user.PreferredLanguage = input.PreferredLanguage is "ar" ? "ar" : "en";
        IdentityResult r = userId is null ? await users.CreateAsync(user, input.Password!) : await users.UpdateAsync(user);
        if (!r.Succeeded) return Error.Validation("USER_SAVE_FAILED", details: r.Errors.Select(e => e.Code).ToArray());
        if (userId is not null && !string.IsNullOrWhiteSpace(input.Password))
        {
            var reset = await users.GeneratePasswordResetTokenAsync(user);
            var pr = await users.ResetPasswordAsync(user, reset, input.Password);
            if (!pr.Succeeded) return Error.Validation("PASSWORD_CHANGE_FAILED", details: pr.Errors.Select(e => e.Code).ToArray());
        }

        var validRoles = await db.AppRoles.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId && input.RoleIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        var current = await db.UserRoleAssignments.IgnoreQueryFilters().Where(a => a.UserId == user.Id && a.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var a in current.Where(a => !validRoles.Contains(a.RoleId))) db.UserRoleAssignments.Remove(a);
        foreach (var roleId in validRoles.Where(rid => current.All(a => a.RoleId != rid)))
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = user.Id, InstitutionId = institutionId, RoleId = roleId });
        await db.SaveChangesAsync(ct);
        return user.Id;
    }

    private async Task<AuthTokens> IssueAsync(AppUser user, string? ip, RefreshToken? rotated, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var accessExpires = now.AddMinutes(_jwt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, user.Email ?? user.Id.ToString()),
        };
        if (user.DisplayNameAr is not null) claims.Add(new Claim(AppClaims.DisplayNameAr, user.DisplayNameAr));
        if (user.DisplayNameEn is not null) claims.Add(new Claim(AppClaims.DisplayNameEn, user.DisplayNameEn));
        if (user.InstructorId is { } iid) claims.Add(new Claim(AppClaims.InstructorId, iid.ToString()));
        if (user.StudentGroupId is { } gid) claims.Add(new Claim(AppClaims.StudentGroupId, gid.ToString()));
        if (user.DefaultInstitutionId is { } did) claims.Add(new Claim(AppClaims.DefaultInstitution, did.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience, claims, now.UtcDateTime, accessExpires.UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var access = new JwtSecurityTokenHandler().WriteToken(token);

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshExpires = now.AddDays(_jwt.RefreshTokenDays);
        var hash = Hash(refresh);
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = hash, CreatedAt = now, ExpiresAt = refreshExpires, CreatedByIp = ip });
        if (rotated is not null)
        {
            rotated.RevokedAt = now;
            rotated.ReplacedByHash = hash;
        }
        await db.SaveChangesAsync(ct);
        return new AuthTokens(access, accessExpires, refresh, refreshExpires);
    }

    private static UserProfileDto ToProfile(AppUser u) => new(u.Id, u.Email ?? string.Empty, u.DisplayNameAr, u.DisplayNameEn, u.PreferredLanguage,
        u.PreferredTheme, u.DigitStyle, u.DefaultInstitutionId, u.InstructorId, u.StudentGroupId, u.IsActive);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string LangKey(Guid userId) => $"user-lang:{userId}";
}
