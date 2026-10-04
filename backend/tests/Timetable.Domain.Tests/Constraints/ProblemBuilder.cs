using Timetable.Domain.Common;
using Timetable.Domain.Constraints;

namespace Timetable.Domain.Tests.Constraints;

/// <summary>Fluent builder for small, explicit scheduling problems used by the constraint tests.</summary>
public sealed class ProblemBuilder
{
    private readonly Dictionary<Guid, GroupInfo> _groups = [];
    private readonly Dictionary<Guid, InstructorInfo> _instructors = [];
    private readonly Dictionary<Guid, RoomInfo> _rooms = [];
    private readonly Dictionary<Guid, SessionInfo> _sessions = [];
    private readonly List<ExternalBusy> _busy = [];
    private readonly Dictionary<(Guid, Guid), int> _travel = [];
    private readonly List<(int, int)> _disabled = [];
    private readonly List<ShiftInfo> _shifts = [];
    private int _weekCycle = 1;
    private IReadOnlyList<(int, int, bool)> _slots = UniversitySlots;

    public static readonly Guid HallType = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid LabType = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    /// <summary>Slots 0,1,2 then a break (3) then 4,5,6 — 90 minutes each, like the university template.</summary>
    public static readonly IReadOnlyList<(int, int, bool)> UniversitySlots =
    [
        (480, 570, false), (570, 660, false), (660, 750, false), (750, 810, true), (810, 900, false), (900, 990, false), (990, 1080, false),
    ];

    public ProblemBuilder WithSlots(IReadOnlyList<(int, int, bool)> slots) { _slots = slots; return this; }
    public ProblemBuilder WithWeekCycle(int n) { _weekCycle = n; return this; }
    public ProblemBuilder Disable(int day, int slot) { _disabled.Add((day, slot)); return this; }

    public Guid Shift(string code, int first, int last)
    {
        var id = Guid.NewGuid();
        _shifts.Add(new ShiftInfo(id, code, first, last));
        return id;
    }

    public Guid Group(string code, int students = 30, Guid? parent = null, string kind = "CLASS", Guid? shift = null, Guid? homeRoom = null,
        Dictionary<string, string>? custom = null)
    {
        var id = Guid.NewGuid();
        _groups[id] = new GroupInfo { Id = id, Code = code, Name = new BiText(null, code), StudentCount = students, ParentId = parent, KindCode = kind, ShiftId = shift,
            HomeRoomId = homeRoom, CustomFields = custom ?? [] };
        return id;
    }

    public Guid Instructor(string code, string type = "DOCTOR", Guid? person = null, decimal? maxDay = null, decimal? maxWeek = null,
        IEnumerable<Guid>? qualified = null, Dictionary<(int, int), AvailabilityStateValue>? availability = null, Dictionary<string, string>? custom = null)
    {
        var id = Guid.NewGuid();
        var av = availability ?? [];
        _instructors[id] = new InstructorInfo { Id = id, Code = code, Name = new BiText(null, code), TypeCode = type, PersonId = person, MaxHoursPerDay = maxDay,
            MaxHoursPerWeek = maxWeek, QualifiedCourseIds = (qualified ?? []).ToHashSet(), Availability = av,
            HasPreferences = av.Values.Any(v => v == AvailabilityStateValue.Preferred), CustomFields = custom ?? [] };
        return id;
    }

    public Guid Room(string code, int capacity = 100, Guid? type = null, string typeCode = "HALL", IEnumerable<string>? equipment = null, Guid? building = null,
        IEnumerable<(int, int)>? unavailable = null)
    {
        var id = Guid.NewGuid();
        _rooms[id] = new RoomInfo { Id = id, Code = code, Name = new BiText(null, code), Capacity = capacity, RoomTypeId = type ?? HallType, RoomTypeCode = typeCode,
            Equipment = (equipment ?? []).ToHashSet(), BuildingId = building, Unavailable = (unavailable ?? []).ToHashSet() };
        return id;
    }

    public ProblemBuilder Busy(Guid person, int day, int start, int end) { _busy.Add(new ExternalBusy(person, day, start, end, new BiText(null, "Other"))); return this; }
    public ProblemBuilder Travel(Guid a, Guid b, int minutes) { _travel[(a, b)] = minutes; return this; }

    public Guid Session(string course, IEnumerable<Guid> groups, Guid? instructor = null, int duration = 1, int perWeek = 1, string type = "LEC",
        Guid? roomType = null, IEnumerable<string>? equipment = null, IEnumerable<string>? courseTags = null, int weekMask = 0, IEnumerable<Guid>? pool = null,
        Guid? courseId = null, bool requiresRoom = true, bool requiresInstructor = true, IEnumerable<int>? allowedDays = null, int? slotFrom = null, int? slotTo = null,
        IEnumerable<string>? allowedInstructorTypes = null, IEnumerable<string>? tags = null, Dictionary<string, string>? custom = null)
    {
        var id = Guid.NewGuid();
        var gids = groups.ToList();
        _sessions[id] = new SessionInfo
        {
            Id = id, CourseId = courseId ?? CourseId(course), CourseCode = course, CourseName = new BiText(null, course), CourseTags = (courseTags ?? []).ToHashSet(),
            SessionTypeId = Guid.NewGuid(), SessionTypeCode = type, SessionTypeName = new BiText(null, type), DurationSlots = duration, SessionsPerWeek = perWeek,
            RequiredRoomTypeId = roomType, RequiredEquipment = (equipment ?? []).ToHashSet(), FixedInstructorId = instructor, CandidateInstructorIds = (pool ?? []).ToList(),
            GroupIds = gids, WeekMask = weekMask, RequiresRoom = requiresRoom, RequiresInstructor = requiresInstructor, AllowedDays = (allowedDays ?? []).ToList(),
            AllowedSlotFrom = slotFrom, AllowedSlotTo = slotTo, AllowedInstructorTypeCodes = (allowedInstructorTypes ?? []).ToList(), Tags = (tags ?? []).ToHashSet(),
            CustomFields = custom ?? [], StudentCount = gids.Sum(g => _groups.TryGetValue(g, out var gi) ? gi.StudentCount : 0),
        };
        return id;
    }

    private readonly Dictionary<string, Guid> _courses = [];
    public Guid CourseId(string code) => _courses.TryGetValue(code, out var id) ? id : _courses[code] = Guid.NewGuid();

    public ScheduleProblem Build()
    {
        ScheduleProblem.ComputeConflictSets(_groups);
        return new ScheduleProblem
        {
            InstitutionId = Guid.NewGuid(),
            Grid = new TimeGrid([0, 1, 2, 3, 4], _slots, _disabled, _shifts, _weekCycle),
            Sessions = _sessions, Groups = _groups, Instructors = _instructors, Rooms = _rooms, ExternalBusy = _busy, TravelMinutes = _travel,
            ShiftCodes = _shifts.ToDictionary(s => s.Id, s => s.Code),
        };
    }
}

public static class T
{
    public static Placement P(Guid session, int day, int slot, Guid? room = null, Guid? instructor = null, int duration = 1, int week = 0, int occurrence = 0, bool entry = true) => new()
    {
        EntryId = entry ? Guid.NewGuid() : null, SessionId = session, Occurrence = occurrence, Day = day, StartSlot = slot, Duration = duration,
        RoomId = room, InstructorId = instructor, WeekMask = week,
    };

    public static ConstraintInstance Hard(IConstraint c, string? json = null) =>
        new(c, ViolationSeverity.Hard, 1, new ConstraintParameters(json, c.Descriptor.ParameterSchema));

    public static ConstraintInstance Soft(IConstraint c, int weight = 1, string? json = null) =>
        new(c, ViolationSeverity.Soft, weight, new ConstraintParameters(json, c.Descriptor.ParameterSchema));

    public static IReadOnlyList<Violation> All(ConstraintInstance i, ScheduleProblem problem, params Placement[] placements)
    {
        var sink = new ViolationCollector(deduplicate: true);
        i.Constraint.EvaluateAll(i, new ScheduleState(problem, placements), sink);
        return sink.Items;
    }

    public static IReadOnlyList<Violation> Candidate(ConstraintInstance i, ScheduleProblem problem, Placement candidate, params Placement[] existing)
    {
        var sink = new ViolationCollector();
        i.Constraint.EvaluateCandidate(i, new ScheduleState(problem, existing), candidate, sink);
        return sink.Items;
    }

    public static decimal Penalty(this IEnumerable<Violation> v) => v.Where(x => x.Severity == ViolationSeverity.Soft).Sum(x => x.Penalty);
    public static int HardCount(this IEnumerable<Violation> v) => v.Count(x => x.Severity == ViolationSeverity.Hard);
}
