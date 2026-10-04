using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Timetable.Application.Abstractions;
using Timetable.Domain.Scheduling;
using Timetable.Infrastructure.Persistence;

namespace Timetable.Api.IntegrationTests.Infrastructure;

/// <summary>Direct database access for arranging test scenarios.</summary>
public static class DataHelper
{
    public static async Task<T> WithDb<T>(this TimetableApiFactory f, Func<AppDbContext, Task<T>> action)
    {
        using var scope = f.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        using var _ = tenant.Bypass();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static Task<Guid> InstitutionId(this TimetableApiFactory f, string code) =>
        f.WithDb(db => db.Institutions.Where(i => i.Code == code).Select(i => i.Id).FirstAsync());

    /// <summary>Creates a fresh draft schedule for the institution's current term.</summary>
    public static Task<Guid> NewDraft(this TimetableApiFactory f, string code, string name) => f.WithDb(async db =>
    {
        var inst = await db.Institutions.FirstAsync(i => i.Code == code);
        var term = await db.AcademicTerms.Where(t => t.InstitutionId == inst.Id).OrderByDescending(t => t.IsCurrent).FirstAsync();
        var s = new Schedule { InstitutionId = inst.Id, TermId = term.Id, Name = name };
        db.Schedules.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    });

    public static Task<Guid> Place(this TimetableApiFactory f, Guid scheduleId, Guid sessionId, int day, int slot, Guid? roomId = null, int occurrence = 0) => f.WithDb(async db =>
    {
        var session = await db.Sessions.FirstAsync(s => s.Id == sessionId);
        var e = new ScheduleEntry
        {
            ScheduleId = scheduleId, SessionId = sessionId, OccurrenceIndex = occurrence, DayOfWeek = day, StartSlot = slot, DurationSlots = session.DurationSlots,
            RoomId = roomId, InstructorId = session.InstructorId ?? (session.CandidateInstructorIds.Count > 0 ? session.CandidateInstructorIds[0] : null),
            WeekMask = session.WeekMask,
        };
        db.ScheduleEntries.Add(e);
        await db.SaveChangesAsync();
        f.Services.GetRequiredService<Timetable.Application.Features.Scheduling.ScheduleStateStore>().Invalidate(scheduleId);
        return e.Id;
    });
}
