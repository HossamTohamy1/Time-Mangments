using Timetable.Domain.Common;

namespace Timetable.Domain.Scheduling;

public sealed class Schedule : AuditableEntity, IInstitutionScoped, ISoftDelete, IHasRowVersion
{
    public Guid InstitutionId { get; set; }
    public Guid TermId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Draft;
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    /// <summary>Frozen copy of the effective configuration used to generate/validate (keeps old schedules explainable).</summary>
    public string? ConfigSnapshotJson { get; set; }
    public decimal? SoftScore { get; set; }
    public int? HardViolations { get; set; }
    public Guid? SourceScheduleId { get; set; }
    public Guid? GeneratedByJobId { get; set; }
    /// <summary>Set while a generation job writes into this schedule; manual edits are rejected meanwhile.</summary>
    public Guid? LockedByJobId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<ScheduleEntry> Entries { get; set; } = [];

    public bool IsEditable => Status == ScheduleStatus.Draft && LockedByJobId is null;

    public Result EnsureEditable()
    {
        if (LockedByJobId is not null) return Error.Conflict("SCHEDULE_LOCKED");
        if (Status != ScheduleStatus.Draft) return Error.Conflict("SCHEDULE_NOT_DRAFT", new Dictionary<string, object?> { ["status"] = Status.ToString() });
        return Result.Success();
    }

    public void Publish(string? by, DateTimeOffset now)
    {
        Status = ScheduleStatus.Published;
        PublishedAt = now;
        PublishedBy = by;
    }
}

/// <summary>One placed occurrence of a session.</summary>
public sealed class ScheduleEntry : AuditableEntity, IHasRowVersion
{
    public Guid ScheduleId { get; set; }
    public Guid SessionId { get; set; }
    public int OccurrenceIndex { get; set; }
    public int DayOfWeek { get; set; }
    public int StartSlot { get; set; }
    public int DurationSlots { get; set; } = 1;
    public Guid? RoomId { get; set; }
    /// <summary>Chosen instructor (the fixed one, or one picked from the session's pool).</summary>
    public Guid? InstructorId { get; set; }
    public int WeekMask { get; set; }
    public bool Pinned { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A one-off change (cancel / move / substitute) on a date without altering the base entry.</summary>
public sealed class ScheduleException : AuditableEntity
{
    public Guid ScheduleId { get; set; }
    public Guid ScheduleEntryId { get; set; }
    public DateOnly Date { get; set; }
    public ScheduleExceptionKind Kind { get; set; }
    public int? NewDayOfWeek { get; set; }
    public int? NewStartSlot { get; set; }
    public Guid? NewRoomId { get; set; }
    public Guid? SubstituteInstructorId { get; set; }
    public Guid? SubstitutionId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Append-only log of schedule mutations used for history and undo/redo.</summary>
public sealed class ScheduleChange : Entity
{
    public Guid ScheduleId { get; set; }
    public long Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = "[]";
    public string AfterJson { get; set; } = "[]";
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset At { get; set; }
    public bool Undone { get; set; }
}

public sealed class GenerationJob : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public Guid TermId { get; set; }
    public Guid? BaseScheduleId { get; set; }
    public Guid? ResultScheduleId { get; set; }
    public GenerationJobStatus Status { get; set; }
    public string Engine { get; set; } = "cpsat";
    public string RequestJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public int Progress { get; set; }
    public decimal? BestScore { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorCode { get; set; }
}
