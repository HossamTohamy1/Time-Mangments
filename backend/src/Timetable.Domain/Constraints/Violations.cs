namespace Timetable.Domain.Constraints;

public enum ViolationSeverity { Soft = 1, Hard = 2 }

public enum EntityRefKind { Entry = 0, Session = 1, Instructor = 2, Room = 3, Group = 4, Course = 5, External = 6 }

public sealed record EntityRef(EntityRefKind Kind, Guid Id);

/// <summary>A constraint finding: hard violation or soft penalty, with a stable message code and structured parameters.</summary>
public sealed record Violation
{
    /// <summary>Constraint (or rule) code that produced it.</summary>
    public required string ConstraintCode { get; init; }
    /// <summary>Localizable message code, e.g. INSTRUCTOR_DOUBLE_BOOKED.</summary>
    public required string MessageCode { get; init; }
    public required ViolationSeverity Severity { get; init; }
    /// <summary>Soft penalty contribution (weight × amount). 0 for hard violations. May be negative for candidate deltas.</summary>
    public decimal Penalty { get; init; }
    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<EntityRef> Entities { get; init; } = [];
    /// <summary>Day/slot the finding is anchored to (when meaningful).</summary>
    public int? Day { get; init; }
    public int? Slot { get; init; }

    /// <summary>Deterministic key for de-duplicating pairwise findings.</summary>
    public string Key => $"{ConstraintCode}|{MessageCode}|{Day}|{Slot}|{string.Join(',', Entities.Select(e => $"{(int)e.Kind}:{e.Id}").Order())}";
}

public interface IViolationSink
{
    void Add(Violation v);
}

public sealed class ViolationCollector : IViolationSink
{
    private readonly List<Violation> _items = [];
    private readonly HashSet<string>? _seen;

    public ViolationCollector(bool deduplicate = false) => _seen = deduplicate ? [] : null;

    public IReadOnlyList<Violation> Items => _items;
    public bool HasHard => _items.Any(v => v.Severity == ViolationSeverity.Hard);
    public decimal Penalty => _items.Where(v => v.Severity == ViolationSeverity.Soft).Sum(v => v.Penalty);

    public void Add(Violation v)
    {
        if (v.Severity == ViolationSeverity.Soft && v.Penalty == 0) return;
        if (_seen is not null && !_seen.Add(v.Key)) return;
        _items.Add(v);
    }
}

/// <summary>Short-circuits after the first hard violation (fast feasibility checks in solvers).</summary>
public sealed class HardStopSink : IViolationSink
{
    public bool HasHard { get; private set; }
    public decimal Penalty { get; private set; }

    public void Add(Violation v)
    {
        if (v.Severity == ViolationSeverity.Hard) HasHard = true;
        else Penalty += v.Penalty;
    }
}
