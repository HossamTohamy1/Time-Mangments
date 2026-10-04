using Timetable.Application.Abstractions;
using Timetable.Domain.Constraints;

namespace Timetable.Application.Features.Scheduling;

public sealed record EntityRefDto(string Kind, Guid Id);

/// <summary>A constraint finding with a stable code, localized message and rendered parameters.</summary>
public sealed record ViolationDto(string ConstraintCode, string Code, string Severity, decimal Penalty, string Message,
    IReadOnlyDictionary<string, string> Params, IReadOnlyList<EntityRefDto> Entities, int? Day, int? Slot);

public sealed record ValidationReportDto(Guid ScheduleId, int HardCount, decimal SoftPenalty, int UnplacedOccurrences, IReadOnlyList<ViolationDto> Violations);

public sealed record CandidateDto(string Status, decimal Penalty, IReadOnlyList<ViolationDto> Violations);

public sealed record SlotOptionDto(int Day, int StartSlot, string Status, Guid? RoomId, Guid? InstructorId, decimal Penalty, int ValidAlternatives,
    IReadOnlyList<ViolationDto> Reasons);

public static class ViolationMapper
{
    public static ViolationDto Map(Violation v, IMessageLocalizer localizer, string lang)
    {
        var rendered = v.Params.ToDictionary(kv => kv.Key, kv => Render(kv.Value, lang));
        return new ViolationDto(v.ConstraintCode, v.MessageCode, v.Severity.ToString(), v.Penalty,
            localizer.Localize(v.MessageCode, v.Params.ToDictionary(kv => kv.Key, kv => (object?)Render(kv.Value, lang)), lang),
            rendered, v.Entities.Select(e => new EntityRefDto(e.Kind.ToString(), e.Id)).Distinct().ToList(), v.Day, v.Slot);
    }

    private static string Render(object? value, string lang) => value switch
    {
        null => string.Empty,
        BiText b => b.For(lang),
        decimal d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string Status(SlotStatus s) => s switch { SlotStatus.Valid => "valid", SlotStatus.ValidWithPenalty => "penalty", _ => "invalid" };
}

public static class SessionLabels
{
    /// <summary>"CS201 · Lab · CS-Y3-A1": course code, localized session type and groups — unique enough to tell occurrences apart.</summary>
    public static string Describe(ScheduleProblem problem, SessionInfo s, string lang)
    {
        var type = s.SessionTypeName.For(lang);
        var groups = string.Join(", ", s.GroupIds.Select(g => problem.Groups.TryGetValue(g, out var x) ? x.Code : null).Where(c => c is not null));
        return string.Join(" · ", new[] { s.CourseCode, type, groups }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
