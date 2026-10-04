using Microsoft.AspNetCore.Identity;

namespace Timetable.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public string? DisplayNameAr { get; set; }
    public string? DisplayNameEn { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public string PreferredTheme { get; set; } = "system";
    public string DigitStyle { get; set; } = "western";
    public Guid? DefaultInstitutionId { get; set; }
    public Guid? InstructorId { get; set; }
    public Guid? StudentGroupId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
