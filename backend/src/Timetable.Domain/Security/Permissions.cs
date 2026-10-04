namespace Timetable.Domain.Security;

/// <summary>Granular permission catalogue (code). Roles are data that reference these codes.</summary>
public static class Permissions
{
    public const string DashboardView = "dashboard.view";
    public const string TimetableView = "timetable.view";
    public const string TimetableViewOwn = "timetable.view.own";
    public const string TimetableEdit = "timetable.edit";
    public const string SchedulePublish = "schedule.publish";
    public const string ScheduleGenerate = "schedule.generate";
    public const string ResourcesView = "resources.view";
    public const string ResourcesManage = "resources.manage";
    public const string AvailabilityManage = "availability.manage";
    public const string AvailabilityManageOwn = "availability.manage.own";
    public const string ConfigManage = "config.manage";
    public const string RulesManage = "rules.manage";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string ExportsRun = "exports.run";
    public const string ImportsRun = "imports.run";
    public const string SubstitutionsManage = "substitutions.manage";
    public const string AuditView = "audit.view";
    public const string InstitutionsManage = "institutions.manage";

    public static readonly IReadOnlyList<string> All =
    [
        DashboardView, TimetableView, TimetableViewOwn, TimetableEdit, SchedulePublish, ScheduleGenerate,
        ResourcesView, ResourcesManage, AvailabilityManage, AvailabilityManageOwn, ConfigManage, RulesManage,
        UsersManage, RolesManage, ExportsRun, ImportsRun, SubstitutionsManage, AuditView, InstitutionsManage,
    ];

    /// <summary>Permission groups for the roles matrix UI.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["timetable"] = [DashboardView, TimetableView, TimetableViewOwn, TimetableEdit, SchedulePublish, ScheduleGenerate],
        ["resources"] = [ResourcesView, ResourcesManage, AvailabilityManage, AvailabilityManageOwn, SubstitutionsManage],
        ["configuration"] = [ConfigManage, RulesManage, InstitutionsManage],
        ["administration"] = [UsersManage, RolesManage, AuditView, ExportsRun, ImportsRun],
    };
}
