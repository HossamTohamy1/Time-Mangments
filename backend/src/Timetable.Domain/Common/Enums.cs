namespace Timetable.Domain.Common;

// Only purely technical enums live here. Business concepts (session types, room types, ...) are data.

public enum ScheduleStatus { Draft = 0, Published = 1, Archived = 2 }

public enum ConstraintSeverity { Off = 0, Soft = 1, Hard = 2 }

public enum AvailabilityState { Available = 0, Unavailable = 1, Preferred = 2 }

public enum CustomFieldDataType { Text = 0, Number = 1, Boolean = 2, Date = 3, SingleSelect = 4, MultiSelect = 5 }

/// <summary>Technical entity kinds that support custom fields.</summary>
public enum CustomFieldEntity { Instructor = 0, Room = 1, Course = 2, Group = 3, Session = 4 }

public enum ScheduleExceptionKind { Cancel = 0, Move = 1, Substitute = 2 }

public enum SubstitutionStatus { Open = 0, Resolved = 1, Cancelled = 2 }

public enum CalendarDayKind { Holiday = 0, Exception = 1 }

public enum GenerationJobStatus { Queued = 0, Running = 1, Succeeded = 2, Failed = 3, Cancelled = 4, Infeasible = 5 }

public enum ChangeAction { Created = 0, Updated = 1, Deleted = 2 }
