namespace Timetable.Domain.Common;

/// <summary>Base entity with a sortable GUID v7 identifier.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>Audit fields populated automatically by the persistence layer.</summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>Soft-deleted rows are hidden by a global query filter and kept for history.</summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Row belongs to one institution (tenant).</summary>
public interface IInstitutionScoped
{
    Guid InstitutionId { get; set; }
}

/// <summary>Bilingual master data: at least one of the names is required.</summary>
public interface IBilingual
{
    string? NameAr { get; set; }
    string? NameEn { get; set; }
}

/// <summary>Entity that carries free-form tags and dynamic custom field values.</summary>
public interface IHasTagsAndCustomFields
{
    List<string> Tags { get; set; }
    string? CustomFieldsJson { get; set; }
}

/// <summary>Optimistic concurrency token.</summary>
public interface IHasRowVersion
{
    byte[] RowVersion { get; set; }
}

public static class BilingualExtensions
{
    /// <summary>Returns the name in the requested language, falling back to the other language.</summary>
    public static string DisplayName(this IBilingual b, string lang)
    {
        var primary = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? b.NameAr : b.NameEn;
        var fallback = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? b.NameEn : b.NameAr;
        return !string.IsNullOrWhiteSpace(primary) ? primary! : fallback ?? string.Empty;
    }
}
