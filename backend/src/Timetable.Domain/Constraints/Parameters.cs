using System.Text.Json;

namespace Timetable.Domain.Constraints;

public enum ParameterType { Int, Bool, String, StringList, IntList, Decimal }

/// <summary>Describes one configurable parameter of a constraint (used for validation and dynamic UI forms).</summary>
public sealed record ParameterDescriptor(
    string Name,
    ParameterType Type,
    object? Default = null,
    decimal? Min = null,
    decimal? Max = null,
    /// <summary>UI hint for which lookup the values come from: sessionType, tag, instructorType, roomType, slot, day.</summary>
    string? Source = null,
    bool Required = false);

/// <summary>Typed, read-only view over a constraint's JSON parameters with schema defaults.</summary>
public sealed class ConstraintParameters
{
    private readonly Dictionary<string, JsonElement> _values;
    private readonly IReadOnlyList<ParameterDescriptor> _schema;

    public ConstraintParameters(string? json, IReadOnlyList<ParameterDescriptor> schema)
    {
        _schema = schema;
        _values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var p in doc.RootElement.EnumerateObject()) _values[p.Name] = p.Value.Clone();
    }

    public static ConstraintParameters Empty { get; } = new(null, []);

    private ParameterDescriptor? Desc(string name) => _schema.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public int GetInt(string name, int fallback = 0) =>
        _values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
        : Desc(name)?.Default is { } d ? Convert.ToInt32(d, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public decimal GetDecimal(string name, decimal fallback = 0) =>
        _values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal()
        : Desc(name)?.Default is { } d ? Convert.ToDecimal(d, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public bool GetBool(string name, bool fallback = false) =>
        _values.TryGetValue(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean()
        : Desc(name)?.Default is bool d ? d : fallback;

    public string? GetString(string name) =>
        _values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
        : Desc(name)?.Default as string;

    public IReadOnlyList<string> GetStrings(string name) =>
        _values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : Desc(name)?.Default as IReadOnlyList<string> ?? [];

    public IReadOnlyList<int> GetInts(string name) =>
        _values.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToList()
            : Desc(name)?.Default as IReadOnlyList<int> ?? [];

    /// <summary>Validates raw JSON against a schema; returns error codes keyed by parameter name.</summary>
    public static IReadOnlyDictionary<string, string> Validate(string? json, IReadOnlyList<ParameterDescriptor> schema)
    {
        var errors = new Dictionary<string, string>();
        Dictionary<string, JsonElement> values = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { errors["$"] = "PARAMS_NOT_OBJECT"; return errors; }
                foreach (var p in doc.RootElement.EnumerateObject()) values[p.Name] = p.Value.Clone();
            }
            catch (JsonException) { errors["$"] = "PARAMS_INVALID_JSON"; return errors; }
        }
        foreach (var name in values.Keys.Where(k => schema.All(d => !d.Name.Equals(k, StringComparison.OrdinalIgnoreCase))))
            errors[name] = "PARAM_UNKNOWN";
        foreach (var d in schema)
        {
            if (!values.TryGetValue(d.Name, out var v))
            {
                if (d.Required && d.Default is null) errors[d.Name] = "PARAM_REQUIRED";
                continue;
            }
            var ok = d.Type switch
            {
                ParameterType.Int => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _),
                ParameterType.Decimal => v.ValueKind == JsonValueKind.Number,
                ParameterType.Bool => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                ParameterType.String => v.ValueKind == JsonValueKind.String,
                ParameterType.StringList => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String),
                ParameterType.IntList => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number),
                _ => false,
            };
            if (!ok) { errors[d.Name] = "PARAM_WRONG_TYPE"; continue; }
            if (d.Type is ParameterType.Int or ParameterType.Decimal)
            {
                var n = v.GetDecimal();
                if ((d.Min is { } min && n < min) || (d.Max is { } max && n > max)) errors[d.Name] = "PARAM_OUT_OF_RANGE";
            }
        }
        return errors;
    }
}
