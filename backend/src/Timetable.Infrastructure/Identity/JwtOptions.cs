namespace Timetable.Infrastructure.Identity;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "timetable";
    public string Audience { get; set; } = "timetable-web";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
}

public static class AppClaims
{
    public const string DisplayNameAr = "name_ar";
    public const string DisplayNameEn = "name_en";
    public const string InstructorId = "instructor_id";
    public const string StudentGroupId = "group_id";
    public const string DefaultInstitution = "default_inst";
}
