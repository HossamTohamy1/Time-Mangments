using Timetable.Domain.Common;

namespace Timetable.Domain.Organization;

/// <summary>Tenant. Remembers the template it was created from but has no type-specific behaviour.</summary>
public sealed class Institution : AuditableEntity, IBilingual, ISoftDelete
{
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public string? TemplateCode { get; set; }
    public string DefaultLanguage { get; set; } = "en";
    public string TimeZone { get; set; } = "Africa/Cairo";
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class OrgUnit : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete
{
    public Guid InstitutionId { get; set; }
    public Guid OrgUnitTypeId { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public int SortOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class StudentGroup : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete, IHasTagsAndCustomFields
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public Guid? OrgUnitId { get; set; }
    public Guid GroupKindId { get; set; }
    public Guid? ParentGroupId { get; set; }
    public int StudentCount { get; set; }
    public Guid? HomeRoomId { get; set; }
    public Guid? ShiftId { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? CustomFieldsJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Building : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public string? Zone { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Walking/travel time between two buildings in minutes (symmetric).</summary>
public sealed class BuildingTravelTime : Entity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public Guid FromBuildingId { get; set; }
    public Guid ToBuildingId { get; set; }
    public int Minutes { get; set; }
}
