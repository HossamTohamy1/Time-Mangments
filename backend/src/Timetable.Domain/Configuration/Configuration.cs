using Timetable.Domain.Common;

namespace Timetable.Domain.Configuration;

/// <summary>Versioned JSON bundle (lookups, terminology, time, constraints, flags, roles) used to bootstrap an institution.</summary>
public sealed class InstitutionTemplate : AuditableEntity, IBilingual
{
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public string? DescriptionAr { get; set; }
    public string? DescriptionEn { get; set; }
    public int Version { get; set; } = 1;
    public bool IsBuiltIn { get; set; }
    public string BundleJson { get; set; } = "{}";
}

/// <summary>Per-institution rename of any UI term in one language.</summary>
public sealed class TerminologyOverride : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Value { get; set; } = string.Empty;
}

/// <summary>How a catalogue constraint is used by one institution.</summary>
public sealed class ConstraintSetting : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public string ConstraintCode { get; set; } = string.Empty;
    public ConstraintSeverity Severity { get; set; }
    public int Weight { get; set; } = 1;
    public string ParametersJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
}

/// <summary>No-code custom rule (declarative JSON: scope + condition + effect).</summary>
public sealed class RuleDefinition : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public ConstraintSeverity Severity { get; set; } = ConstraintSeverity.Hard;
    public int Weight { get; set; } = 5;
    public string DefinitionJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class CustomFieldDefinition : AuditableEntity, IInstitutionScoped, IBilingual
{
    public Guid InstitutionId { get; set; }
    public CustomFieldEntity EntityType { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public CustomFieldDataType DataType { get; set; }
    public bool Required { get; set; }
    public bool Searchable { get; set; }
    public bool ShowInTable { get; set; }
    public bool Importable { get; set; } = true;
    /// <summary>Options for select types: [{ "value": "x", "labelAr": "..", "labelEn": ".." }].</summary>
    public string? OptionsJson { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class FeatureFlag : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

/// <summary>A named set of permissions, scoped to an institution (roles are data; policies check permissions).</summary>
public sealed class Role : AuditableEntity, IInstitutionScoped, IBilingual
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public bool IsSystem { get; set; }
    public List<string> Permissions { get; set; } = [];
}

/// <summary>Grants a role to a user in an institution, optionally restricted to an org unit subtree.</summary>
public sealed class UserRoleAssignment : Entity, IInstitutionScoped
{
    public Guid UserId { get; set; }
    public Guid InstitutionId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? OrgUnitId { get; set; }
}

/// <summary>Audit trail of configuration changes (who, when, before, after).</summary>
public sealed class ConfigAuditEntry : Entity
{
    public Guid? InstitutionId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public ChangeAction Action { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class Substitution : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public Guid ScheduleId { get; set; }
    public Guid AbsentInstructorId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string? Reason { get; set; }
    public SubstitutionStatus Status { get; set; }
}

/// <summary>In-app notification. Text is rendered from <see cref="Type"/> + params in the reader's language.</summary>
public sealed class Notification : Entity
{
    public Guid UserId { get; set; }
    public Guid? InstitutionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string ParamsJson { get; set; } = "{}";
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
