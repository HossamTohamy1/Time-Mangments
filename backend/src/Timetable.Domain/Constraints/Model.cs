namespace Timetable.Domain.Constraints;

/// <summary>Bilingual text carried in violation parameters; rendered in the reader's language.</summary>
public sealed record BiText(string? Ar, string? En)
{
    public string For(string lang) =>
        lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(Ar) ? En ?? string.Empty : Ar)
            : (string.IsNullOrWhiteSpace(En) ? Ar ?? string.Empty : En);

    public override string ToString() => En ?? Ar ?? string.Empty;
}

/// <summary>Week-cycle bit masks. 0 means "every week"; bit i set means week i of an N-week rotation.</summary>
public static class WeekMask
{
    public const int Every = 0;

    public static bool Overlaps(int a, int b) => a == Every || b == Every || (a & b) != 0;

    public static bool Includes(int mask, int week) => mask == Every || (mask & (1 << week)) != 0;

    public static int Normalize(int mask, int cycleLength)
    {
        if (cycleLength <= 1) return Every;
        var all = (1 << cycleLength) - 1;
        var m = mask & all;
        return m == all || m == 0 ? Every : m;
    }
}

public sealed record ShiftInfo(Guid Id, string Code, int FirstSlot, int LastSlot);

/// <summary>Discrete time grid: working days × slots, breaks, day overrides, shifts and week cycle.</summary>
public sealed class TimeGrid
{
    private readonly HashSet<(int Day, int Slot)> _disabled;
    private readonly Dictionary<(int Day, int Slot), (int Start, int End)> _overrideMinutes;

    public TimeGrid(
        IReadOnlyList<int> days,
        IReadOnlyList<(int StartMinute, int EndMinute, bool IsBreak)> slots,
        IEnumerable<(int Day, int Slot)>? disabled = null,
        IReadOnlyList<ShiftInfo>? shifts = null,
        int weekCycleLength = 1,
        IReadOnlyDictionary<(int Day, int Slot), (int Start, int End)>? overrideMinutes = null)
    {
        Days = days;
        SlotCount = slots.Count;
        StartMinute = slots.Select(s => s.StartMinute).ToArray();
        EndMinute = slots.Select(s => s.EndMinute).ToArray();
        IsBreak = slots.Select(s => s.IsBreak).ToArray();
        _disabled = disabled is null ? [] : [.. disabled];
        Shifts = shifts ?? [];
        WeekCycleLength = Math.Max(1, weekCycleLength);
        _overrideMinutes = overrideMinutes?.ToDictionary(k => k.Key, v => v.Value) ?? [];
        DayOrder = days.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
    }

    public IReadOnlyList<int> Days { get; }
    public IReadOnlyDictionary<int, int> DayOrder { get; }
    public int SlotCount { get; }
    public int[] StartMinute { get; }
    public int[] EndMinute { get; }
    public bool[] IsBreak { get; }
    public IReadOnlyList<ShiftInfo> Shifts { get; }
    public int WeekCycleLength { get; }

    public bool IsWorkingDay(int day) => DayOrder.ContainsKey(day);

    /// <summary>A slot that sessions may occupy on this day (working day, exists, not a break, not disabled).</summary>
    public bool IsUsable(int day, int slot) =>
        slot >= 0 && slot < SlotCount && IsWorkingDay(day) && !IsBreak[slot] && !_disabled.Contains((day, slot));

    /// <summary>All slots [start, start+duration) usable; since breaks are unusable this also prevents crossing breaks.</summary>
    public bool FitsConsecutive(int day, int start, int duration)
    {
        if (duration < 1) return false;
        for (var s = start; s < start + duration; s++)
            if (!IsUsable(day, s)) return false;
        return true;
    }

    public (int Start, int End) Minutes(int day, int slot) =>
        _overrideMinutes.TryGetValue((day, slot), out var m) ? m : (StartMinute[slot], EndMinute[slot]);

    /// <summary>Usable slots of a day (ordered), used for first/last detection.</summary>
    public IEnumerable<int> UsableSlots(int day)
    {
        for (var s = 0; s < SlotCount; s++)
            if (IsUsable(day, s)) yield return s;
    }

    /// <summary>True when slots a and b are adjacent in time with no break between them.</summary>
    public bool AreAdjacent(int a, int b) => Math.Abs(a - b) == 1;

    public int SlotMinutes(int slot) => EndMinute[slot] - StartMinute[slot];
}

public sealed class SessionInfo
{
    public required Guid Id { get; init; }
    public required Guid CourseId { get; init; }
    public string CourseCode { get; init; } = string.Empty;
    public BiText CourseName { get; init; } = new(null, null);
    public IReadOnlySet<string> CourseTags { get; init; } = new HashSet<string>();
    public required Guid SessionTypeId { get; init; }
    public string SessionTypeCode { get; init; } = string.Empty;
    public BiText SessionTypeName { get; init; } = new(null, null);
    public int DurationSlots { get; init; } = 1;
    public int SessionsPerWeek { get; init; } = 1;
    public Guid? RequiredRoomTypeId { get; init; }
    public IReadOnlySet<string> RequiredEquipment { get; init; } = new HashSet<string>();
    public Guid? FixedInstructorId { get; init; }
    public IReadOnlyList<Guid> CandidateInstructorIds { get; init; } = [];
    public IReadOnlyList<Guid> GroupIds { get; init; } = [];
    public int WeekMask { get; init; }
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, string> CustomFields { get; init; } = new Dictionary<string, string>();
    public bool RequiresRoom { get; init; } = true;
    public bool RequiresInstructor { get; init; } = true;
    public bool CountsTowardLoad { get; init; } = true;
    public decimal LoadMultiplier { get; init; } = 1m;
    public IReadOnlyList<int> AllowedDays { get; init; } = [];
    public int? AllowedSlotFrom { get; init; }
    public int? AllowedSlotTo { get; init; }
    public IReadOnlyList<string> AllowedInstructorTypeCodes { get; init; } = [];
    public int StudentCount { get; init; }

    public string Label => string.IsNullOrEmpty(SessionTypeCode) ? CourseCode : $"{CourseCode} ({SessionTypeCode})";

    /// <summary>Instructor options: fixed one, otherwise the pool (empty = none required/available).</summary>
    public IReadOnlyList<Guid> InstructorOptions =>
        FixedInstructorId is { } f ? [f] : CandidateInstructorIds;
}

public sealed class GroupInfo
{
    public required Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public BiText Name { get; init; } = new(null, null);
    public int StudentCount { get; init; }
    public Guid? ParentId { get; init; }
    public string KindCode { get; init; } = string.Empty;
    public Guid? OrgUnitId { get; init; }
    /// <summary>Org unit and all its ancestors (for rule scopes).</summary>
    public IReadOnlySet<Guid> OrgUnitPath { get; init; } = new HashSet<Guid>();
    public Guid? ShiftId { get; init; }
    public Guid? HomeRoomId { get; init; }
    /// <summary>Groups that cannot meet at the same time: self + ancestors + descendants.</summary>
    public IReadOnlySet<Guid> ConflictSet { get; set; } = new HashSet<Guid>();
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, string> CustomFields { get; init; } = new Dictionary<string, string>();
}

public sealed class InstructorInfo
{
    public required Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public BiText Name { get; init; } = new(null, null);
    public string TypeCode { get; init; } = string.Empty;
    public Guid? PersonId { get; init; }
    public decimal? MaxHoursPerDay { get; init; }
    public decimal? MaxHoursPerWeek { get; init; }
    public IReadOnlySet<Guid> QualifiedCourseIds { get; init; } = new HashSet<Guid>();
    public IReadOnlyDictionary<(int Day, int Slot), AvailabilityStateValue> Availability { get; init; } =
        new Dictionary<(int, int), AvailabilityStateValue>();
    public bool HasPreferences { get; init; }
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, string> CustomFields { get; init; } = new Dictionary<string, string>();

    public AvailabilityStateValue StateAt(int day, int slot) =>
        Availability.TryGetValue((day, slot), out var s) ? s : AvailabilityStateValue.Available;
}

/// <summary>Mirror of <see cref="Common.AvailabilityState"/> kept local so the engine has no persistence coupling.</summary>
public enum AvailabilityStateValue { Available = 0, Unavailable = 1, Preferred = 2 }

public sealed class RoomInfo
{
    public required Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public BiText Name { get; init; } = new(null, null);
    public int Capacity { get; init; }
    public Guid RoomTypeId { get; init; }
    public string RoomTypeCode { get; init; } = string.Empty;
    public IReadOnlySet<string> Equipment { get; init; } = new HashSet<string>();
    public Guid? BuildingId { get; init; }
    public IReadOnlySet<(int Day, int Slot)> Unavailable { get; init; } = new HashSet<(int, int)>();
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
}

/// <summary>Busy interval of a person in another institution (compared in clock minutes, not slots).</summary>
public sealed record ExternalBusy(Guid PersonId, int Day, int StartMinute, int EndMinute, BiText Label);

/// <summary>Immutable snapshot of everything the validator and solver need for one schedule.</summary>
public sealed class ScheduleProblem
{
    public required Guid InstitutionId { get; init; }
    public required TimeGrid Grid { get; init; }
    public required IReadOnlyDictionary<Guid, SessionInfo> Sessions { get; init; }
    public required IReadOnlyDictionary<Guid, GroupInfo> Groups { get; init; }
    public required IReadOnlyDictionary<Guid, InstructorInfo> Instructors { get; init; }
    public required IReadOnlyDictionary<Guid, RoomInfo> Rooms { get; init; }
    public IReadOnlyList<ExternalBusy> ExternalBusy { get; init; } = [];
    public IReadOnlyDictionary<(Guid A, Guid B), int> TravelMinutes { get; init; } = new Dictionary<(Guid, Guid), int>();
    public IReadOnlyDictionary<Guid, string> ShiftCodes { get; init; } = new Dictionary<Guid, string>();

    private ILookup<Guid, ExternalBusy>? _busyByPerson;
    private ILookup<Guid, SessionInfo>? _byCourse;

    public ILookup<Guid, SessionInfo> SessionsByCourse => _byCourse ??= Sessions.Values.ToLookup(s => s.CourseId);

    public ILookup<Guid, ExternalBusy> BusyByPerson => _busyByPerson ??= ExternalBusy.ToLookup(b => b.PersonId);

    public int Travel(Guid? a, Guid? b)
    {
        if (a is null || b is null || a == b) return 0;
        if (TravelMinutes.TryGetValue((a.Value, b.Value), out var m)) return m;
        return TravelMinutes.TryGetValue((b.Value, a.Value), out m) ? m : 0;
    }

    /// <summary>Computes each group's conflict set (self + ancestors + descendants). Call once after construction.</summary>
    public static void ComputeConflictSets(IReadOnlyDictionary<Guid, GroupInfo> groups)
    {
        var children = groups.Values.Where(g => g.ParentId is not null).ToLookup(g => g.ParentId!.Value, g => g.Id);
        foreach (var g in groups.Values)
        {
            var set = new HashSet<Guid> { g.Id };
            var p = g.ParentId;
            var guard = 0;
            while (p is { } pid && groups.TryGetValue(pid, out var parent) && guard++ < 64)
            {
                set.Add(pid);
                p = parent.ParentId;
            }
            var stack = new Stack<Guid>(children[g.Id]);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!set.Add(c)) continue;
                foreach (var cc in children[c]) stack.Push(cc);
            }
            g.ConflictSet = set;
        }
    }
}

/// <summary>A (possibly hypothetical) placement of one session occurrence.</summary>
public sealed class Placement
{
    public Guid? EntryId { get; init; }
    public required Guid SessionId { get; init; }
    public int Occurrence { get; init; }
    public required int Day { get; init; }
    public required int StartSlot { get; init; }
    public int Duration { get; init; } = 1;
    public Guid? RoomId { get; init; }
    public Guid? InstructorId { get; init; }
    public int WeekMask { get; init; }
    public bool Pinned { get; init; }

    public int EndSlot => StartSlot + Duration - 1;

    public bool Covers(int slot) => slot >= StartSlot && slot <= EndSlot;

    public bool SameIdentity(Placement other) =>
        ReferenceEquals(this, other) ||
        (EntryId is not null && EntryId == other.EntryId) ||
        (SessionId == other.SessionId && Occurrence == other.Occurrence && EntryId is null && other.EntryId is null);

    public Placement With(int? day = null, int? start = null, Guid? room = null, bool clearRoom = false, Guid? instructor = null) => new()
    {
        EntryId = EntryId,
        SessionId = SessionId,
        Occurrence = Occurrence,
        Day = day ?? Day,
        StartSlot = start ?? StartSlot,
        Duration = Duration,
        RoomId = clearRoom ? null : room ?? RoomId,
        InstructorId = instructor ?? InstructorId,
        WeekMask = WeekMask,
        Pinned = Pinned,
    };

    public override string ToString() => $"{SessionId:N}#{Occurrence}@{Day}:{StartSlot}+{Duration} room={RoomId} inst={InstructorId}";
}
