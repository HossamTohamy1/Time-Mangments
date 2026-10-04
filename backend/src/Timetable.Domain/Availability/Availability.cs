using Timetable.Domain.Common;

namespace Timetable.Domain.Availability;

/// <summary>Non-default availability cell (the default for a missing cell is Available).</summary>
public sealed class InstructorAvailability : Entity
{
    public Guid InstructorId { get; set; }
    public int DayOfWeek { get; set; }
    public int SlotIndex { get; set; }
    public AvailabilityState State { get; set; }
}

public sealed class RoomAvailability : Entity
{
    public Guid RoomId { get; set; }
    public int DayOfWeek { get; set; }
    public int SlotIndex { get; set; }
    public AvailabilityState State { get; set; }
}
