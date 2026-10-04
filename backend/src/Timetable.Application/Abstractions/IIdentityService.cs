using Timetable.Domain.Common;

namespace Timetable.Application.Abstractions;

public sealed record AuthTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed record UserProfileDto(
    Guid Id, string Email, string? DisplayNameAr, string? DisplayNameEn, string PreferredLanguage, string PreferredTheme, string DigitStyle,
    Guid? DefaultInstitutionId, Guid? InstructorId, Guid? StudentGroupId, bool IsActive);

public sealed record UserListItemDto(Guid Id, string Email, string? DisplayNameAr, string? DisplayNameEn, bool IsActive,
    Guid? InstructorId, Guid? StudentGroupId, IReadOnlyList<UserRoleDto> Roles);

public sealed record UserRoleDto(Guid AssignmentId, Guid RoleId, string RoleCode, Guid? OrgUnitId);

public sealed record UpsertUserInput(string Email, string? DisplayNameAr, string? DisplayNameEn, string? Password, bool IsActive,
    Guid? InstructorId, Guid? StudentGroupId, IReadOnlyList<Guid> RoleIds, string PreferredLanguage = "en");

public sealed record ProfileUpdate(string? PreferredLanguage, string? PreferredTheme, string? DigitStyle, string? DisplayNameAr, string? DisplayNameEn);

/// <summary>Authentication + user management (ASP.NET Core Identity in Infrastructure).</summary>
public interface IIdentityService
{
    Task<Result<AuthTokens>> LoginAsync(string email, string password, string? ip, CancellationToken ct);
    Task<Result<AuthTokens>> RefreshAsync(string refreshToken, string? ip, CancellationToken ct);
    Task RevokeAsync(string refreshToken, CancellationToken ct);
    Task<Result> ChangePasswordAsync(Guid userId, string current, string next, CancellationToken ct);
    Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct);
    Task<Result<UserProfileDto>> UpdateProfileAsync(Guid userId, ProfileUpdate update, CancellationToken ct);
    Task<IReadOnlyList<UserListItemDto>> ListUsersAsync(Guid institutionId, string? search, CancellationToken ct);
    Task<Result<Guid>> UpsertUserAsync(Guid institutionId, Guid? userId, UpsertUserInput input, CancellationToken ct);
    Task<string?> GetPreferredLanguageAsync(Guid userId, CancellationToken ct);
    /// <summary>Active users linked to any of the given instructors or student groups (self-service accounts).</summary>
    Task<IReadOnlyList<Guid>> LinkedUsersAsync(IReadOnlyCollection<Guid> instructorIds, IReadOnlyCollection<Guid> groupIds, CancellationToken ct);
}

public sealed record InstitutionMembership(Guid InstitutionId, string Code, string? NameAr, string? NameEn, IReadOnlyList<string> RoleCodes);

/// <summary>Resolves a user's memberships and effective permissions per institution (cached; invalidated on change).</summary>
public interface IPermissionService
{
    Task<IReadOnlyList<InstitutionMembership>> GetMembershipsAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, Guid institutionId, CancellationToken ct);
    void Invalidate(Guid institutionId);
}
