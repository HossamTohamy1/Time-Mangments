using System.Text.Json;
using System.Text.Json.Serialization;

namespace Timetable.Domain.Constraints.Rules;

[JsonConverter(typeof(JsonStringEnumConverter<RuleEffect>))]
public enum RuleEffect { Forbid = 0, Limit = 1, Prefer = 2 }

/// <summary>Condition templates offered by the no-code Rule Builder.</summary>
public static class RuleConditionTypes
{
    public const string MaxPerDay = "maxPerDay";
    public const string MaxPerWeek = "maxPerWeek";
    public const string MinGap = "minGap";
    public const string SlotRange = "slotRange";
    public const string Days = "days";
    public const string NotAdjacent = "notAdjacent";
    public const string Before = "before";
    public const string After = "after";
    public const string SameRoom = "sameRoom";
    public const string SameDay = "sameDay";
    public const string PreferredTime = "preferredTime";
    public const string AvoidTime = "avoidTime";

    public static readonly IReadOnlyList<string> All =
        [MaxPerDay, MaxPerWeek, MinGap, SlotRange, Days, NotAdjacent, Before, After, SameRoom, SameDay, PreferredTime, AvoidTime];

    public static readonly IReadOnlySet<string> Unary = new HashSet<string> { SlotRange, Days, PreferredTime, AvoidTime };
}

public sealed class CustomFieldFilter
{
    /// <summary>instructor | room | course | group | session</summary>
    public string Entity { get; set; } = "session";
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>Filters selecting which placements a rule applies to. Empty lists mean "any".</summary>
public sealed class RuleScope
{
    public List<string> SessionTypeCodes { get; set; } = [];
    public List<string> CourseTags { get; set; } = [];
    public List<string> SessionTags { get; set; } = [];
    public List<Guid> CourseIds { get; set; } = [];
    public List<string> InstructorTypeCodes { get; set; } = [];
    public List<Guid> InstructorIds { get; set; } = [];
    public List<string> GroupKindCodes { get; set; } = [];
    public List<Guid> GroupIds { get; set; } = [];
    public List<Guid> OrgUnitIds { get; set; } = [];
    public List<string> RoomTypeCodes { get; set; } = [];
    public List<string> ShiftCodes { get; set; } = [];
    public CustomFieldFilter? CustomField { get; set; }
}

public sealed class RuleCondition
{
    public string Type { get; set; } = RuleConditionTypes.MaxPerDay;
    /// <summary>group | instructor | room</summary>
    public string Per { get; set; } = "group";
    public int? Max { get; set; }
    public int? Slots { get; set; }
    /// <summary>1-based slot numbers (as shown in the UI), inclusive.</summary>
    public int? From { get; set; }
    public int? To { get; set; }
    public List<int> Days { get; set; } = [];
    public RuleScope? OtherScope { get; set; }
    public bool SameCourse { get; set; } = true;
}

/// <summary>Declarative, safe rule definition (never code/SQL): scope + condition + effect.</summary>
public sealed class RuleModel
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RuleScope Scope { get; set; } = new();
    public RuleCondition Condition { get; set; } = new();
    public RuleEffect Effect { get; set; } = RuleEffect.Limit;

    public bool IsUnary => RuleConditionTypes.Unary.Contains(Condition.Type);

    public static RuleModel? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RuleModel>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Returns validation error codes keyed by field path (empty = valid).</summary>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var e = new Dictionary<string, string>();
        var c = Condition;
        if (!RuleConditionTypes.All.Contains(c.Type)) { e["condition.type"] = "RULE_UNKNOWN_CONDITION"; return e; }
        if (c.Per is not ("group" or "instructor" or "room")) e["condition.per"] = "RULE_INVALID_PER";
        switch (c.Type)
        {
            case RuleConditionTypes.MaxPerDay or RuleConditionTypes.MaxPerWeek when c.Max is null or < 0:
                e["condition.max"] = "RULE_MAX_REQUIRED"; break;
            case RuleConditionTypes.MinGap when c.Slots is null or < 1:
                e["condition.slots"] = "RULE_SLOTS_REQUIRED"; break;
            case RuleConditionTypes.SlotRange when c.From is null && c.To is null:
                e["condition.from"] = "RULE_RANGE_REQUIRED"; break;
            case RuleConditionTypes.Days when c.Days.Count == 0:
                e["condition.days"] = "RULE_DAYS_REQUIRED"; break;
            case RuleConditionTypes.PreferredTime or RuleConditionTypes.AvoidTime when c.Days.Count == 0 && c.From is null && c.To is null:
                e["condition.from"] = "RULE_TIME_REQUIRED"; break;
            case RuleConditionTypes.NotAdjacent or RuleConditionTypes.Before or RuleConditionTypes.After when c.OtherScope is null:
                e["condition.otherScope"] = "RULE_OTHER_SCOPE_REQUIRED"; break;
        }
        if (c.From is { } f && c.To is { } t && f > t) e["condition.to"] = "RULE_RANGE_INVALID";
        if (c.Days.Any(d => d is < 0 or > 6)) e["condition.days"] = "RULE_DAYS_INVALID";
        return e;
    }
}

/// <summary>Evaluates whether a placement falls inside a rule scope.</summary>
public static class ScopeMatcher
{
    public static bool Matches(RuleScope scope, ScheduleState s, Placement p)
    {
        var session = s.Session(p);
        if (scope.SessionTypeCodes.Count > 0 && !scope.SessionTypeCodes.Contains(session.SessionTypeCode)) return false;
        if (scope.CourseIds.Count > 0 && !scope.CourseIds.Contains(session.CourseId)) return false;
        if (scope.CourseTags.Count > 0 && !scope.CourseTags.Any(session.CourseTags.Contains)) return false;
        if (scope.SessionTags.Count > 0 && !scope.SessionTags.Any(session.Tags.Contains)) return false;
        if (scope.InstructorIds.Count > 0 && (p.InstructorId is null || !scope.InstructorIds.Contains(p.InstructorId.Value))) return false;
        if (scope.InstructorTypeCodes.Count > 0)
        {
            if (p.InstructorId is not { } iid || !s.Problem.Instructors.TryGetValue(iid, out var inst) || !scope.InstructorTypeCodes.Contains(inst.TypeCode)) return false;
        }
        if (scope.RoomTypeCodes.Count > 0)
        {
            if (p.RoomId is not { } rid || !s.Problem.Rooms.TryGetValue(rid, out var room) || !scope.RoomTypeCodes.Contains(room.RoomTypeCode)) return false;
        }
        if (scope.GroupIds.Count > 0 || scope.GroupKindCodes.Count > 0 || scope.OrgUnitIds.Count > 0 || scope.ShiftCodes.Count > 0)
        {
            var groups = session.GroupIds.Select(g => s.Problem.Groups.TryGetValue(g, out var gi) ? gi : null).Where(g => g is not null).ToList();
            bool Any(Func<GroupInfo, bool> f) => groups.Any(g => f(g!));
            if (scope.GroupIds.Count > 0 && !Any(g => scope.GroupIds.Contains(g.Id))) return false;
            if (scope.GroupKindCodes.Count > 0 && !Any(g => scope.GroupKindCodes.Contains(g.KindCode))) return false;
            if (scope.OrgUnitIds.Count > 0 && !Any(g => g.OrgUnitPath.Any(scope.OrgUnitIds.Contains))) return false;
            if (scope.ShiftCodes.Count > 0 && !Any(g => g.ShiftId is { } sid && s.Problem.ShiftCodes.TryGetValue(sid, out var code) && scope.ShiftCodes.Contains(code))) return false;
        }
        if (scope.CustomField is { Key.Length: > 0 } cf && !MatchesCustomField(cf, s, p, session)) return false;
        return true;
    }

    private static bool MatchesCustomField(CustomFieldFilter cf, ScheduleState s, Placement p, SessionInfo session)
    {
        IReadOnlyDictionary<string, string>? values = cf.Entity.ToLowerInvariant() switch
        {
            "instructor" => p.InstructorId is { } i && s.Problem.Instructors.TryGetValue(i, out var inst) ? inst.CustomFields : null,
            "group" => session.GroupIds.Select(g => s.Problem.Groups.TryGetValue(g, out var gi) ? gi.CustomFields : null).FirstOrDefault(v => v?.ContainsKey(cf.Key) == true),
            _ => session.CustomFields,
        };
        return values is not null && values.TryGetValue(cf.Key, out var v) && string.Equals(v, cf.Value, StringComparison.OrdinalIgnoreCase);
    }
}
