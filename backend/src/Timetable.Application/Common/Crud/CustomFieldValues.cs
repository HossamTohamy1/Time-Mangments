using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Behaviors;
using Timetable.Domain.Common;

namespace Timetable.Application.Common.Crud;

/// <summary>Validates dynamic custom field values against the institution's definitions and serializes them.</summary>
public static class CustomFieldValues
{
    public static async Task<Result<string?>> ValidateAsync(IAppDbContext db, CustomFieldEntity entity, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken ct)
    {
        var defs = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.EntityType == entity && d.IsActive).ToListAsync(ct);
        values ??= new Dictionary<string, JsonElement>();
        var clean = new Dictionary<string, object?>();
        foreach (var d in defs)
        {
            var has = values.TryGetValue(d.Key, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                && !(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))
                && !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);
            var field = $"customFields.{d.Key}";
            var p = new Dictionary<string, object> { ["field"] = (object?)(d.NameEn ?? d.NameAr ?? d.Key) ?? d.Key };
            if (!has)
            {
                if (d.Required) return ValidationErrors.Single(field, "CUSTOM_FIELD_REQUIRED", p);
                continue;
            }
            var options = d.OptionsJson is null ? [] : JsonDocument.Parse(d.OptionsJson).RootElement.EnumerateArray()
                .Select(o => o.TryGetProperty("value", out var x) ? x.GetString() : null).Where(x => x is not null).ToHashSet();
            object? parsed = d.DataType switch
            {
                CustomFieldDataType.Text when v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 1000 => v.GetString(),
                CustomFieldDataType.Number when v.ValueKind == JsonValueKind.Number => v.GetDecimal(),
                CustomFieldDataType.Boolean when v.ValueKind is JsonValueKind.True or JsonValueKind.False => v.GetBoolean(),
                CustomFieldDataType.Date when v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CustomFieldDataType.SingleSelect when v.ValueKind == JsonValueKind.String && options.Contains(v.GetString()) => v.GetString(),
                CustomFieldDataType.MultiSelect when v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && options.Contains(x.GetString()))
                    => v.EnumerateArray().Select(x => x.GetString()).ToList(),
                _ => Invalid,
            };
            if (ReferenceEquals(parsed, Invalid)) return ValidationErrors.Single(field, "CUSTOM_FIELD_INVALID", p);
            clean[d.Key] = parsed;
        }
        return clean.Count == 0 ? (string?)null : JsonSerializer.Serialize(clean);
    }

    public static Dictionary<string, JsonElement> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>Flattened string values (used by the constraint engine for rule scopes).</summary>
    public static Dictionary<string, string> Flatten(string? json) =>
        Read(json).ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind switch
        {
            JsonValueKind.String => kv.Value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(",", kv.Value.EnumerateArray().Select(x => x.ToString())),
            _ => kv.Value.ToString(),
        });

    private static readonly object Invalid = new();
}
