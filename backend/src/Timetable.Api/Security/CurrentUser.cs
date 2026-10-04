using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Timetable.Application.Abstractions;
using Timetable.Infrastructure.Identity;

namespace Timetable.Api.Security;

/// <summary>Scoped current user, populated by <see cref="TenantResolutionMiddleware"/>.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name) ?? Principal?.FindFirstValue(JwtRegisteredClaimNames.Email);
    public Guid InstitutionId { get; set; }
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public Guid? InstructorId => Guid.TryParse(Principal?.FindFirstValue(AppClaims.InstructorId), out var id) ? id : null;
    public Guid? StudentGroupId => Guid.TryParse(Principal?.FindFirstValue(AppClaims.StudentGroupId), out var id) ? id : null;
    public string Language => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

    public bool HasPermission(string permission) => Permissions.Contains(permission);
}

/// <summary>Audit user name for EF audit fields.</summary>
public sealed class AuditUserProvider(ICurrentUser user) : Infrastructure.Persistence.IAuditUserProvider
{
    public string? UserName => user.UserName;
}
