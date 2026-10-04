using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Availability;
using Timetable.Domain.Common;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Availability;

public enum AvailabilityTarget { Instructor = 0, Room = 1 }

public sealed record AvailabilityCell(int DayOfWeek, int SlotIndex, AvailabilityState State);

public sealed record AvailabilityDto(AvailabilityTarget Target, Guid Id, IReadOnlyList<AvailabilityCell> Cells);

public sealed record GetAvailabilityQuery(AvailabilityTarget Target, Guid Id) : IQuery<Result<AvailabilityDto>>;

/// <summary>Replaces the non-default cells (missing cell = Available). Self-service when the id is the caller's own instructor.</summary>
public sealed record SaveAvailabilityCommand(AvailabilityTarget Target, Guid Id, IReadOnlyList<AvailabilityCell> Cells) : ICommand<Result<AvailabilityDto>>;

internal sealed class AvailabilityHandlers(IAppDbContext db, ICurrentUser user, IPublisher publisher) :
    IRequestHandler<GetAvailabilityQuery, Result<AvailabilityDto>>,
    IRequestHandler<SaveAvailabilityCommand, Result<AvailabilityDto>>
{
    private bool CanManage(AvailabilityTarget t, Guid id) =>
        user.HasPermission(Permissions.AvailabilityManage)
        || (t == AvailabilityTarget.Instructor && user.InstructorId == id && user.HasPermission(Permissions.AvailabilityManageOwn));

    private bool CanView(AvailabilityTarget t, Guid id) => CanManage(t, id) || user.HasPermission(Permissions.ResourcesView);

    public async Task<Result<AvailabilityDto>> Handle(GetAvailabilityQuery r, CancellationToken ct)
    {
        if (!CanView(r.Target, r.Id)) return Error.Forbidden("PERMISSION_REQUIRED");
        if (!await ExistsAsync(r.Target, r.Id, ct)) return Error.NotFound(r.Target.ToString().ToLowerInvariant(), r.Id);
        return new AvailabilityDto(r.Target, r.Id, await LoadAsync(r.Target, r.Id, ct));
    }

    public async Task<Result<AvailabilityDto>> Handle(SaveAvailabilityCommand r, CancellationToken ct)
    {
        if (!CanManage(r.Target, r.Id)) return Error.Forbidden("PERMISSION_REQUIRED");
        if (!await ExistsAsync(r.Target, r.Id, ct)) return Error.NotFound(r.Target.ToString().ToLowerInvariant(), r.Id);
        if (r.Cells.Any(c => c.DayOfWeek is < 0 or > 6 || c.SlotIndex is < 0 or > 63))
            return Common.Behaviors.ValidationErrors.Single("cells", "VALUE_OUT_OF_RANGE");
        var cells = r.Cells.Where(c => c.State != AvailabilityState.Available).DistinctBy(c => (c.DayOfWeek, c.SlotIndex)).ToList();
        if (r.Target == AvailabilityTarget.Instructor)
        {
            db.InstructorAvailabilities.RemoveRange(await db.InstructorAvailabilities.Where(a => a.InstructorId == r.Id).ToListAsync(ct));
            db.InstructorAvailabilities.AddRange(cells.Select(c => new InstructorAvailability { InstructorId = r.Id, DayOfWeek = c.DayOfWeek, SlotIndex = c.SlotIndex, State = c.State }));
        }
        else
        {
            // Rooms only distinguish available / unavailable.
            db.RoomAvailabilities.RemoveRange(await db.RoomAvailabilities.Where(a => a.RoomId == r.Id).ToListAsync(ct));
            db.RoomAvailabilities.AddRange(cells.Where(c => c.State == AvailabilityState.Unavailable)
                .Select(c => new RoomAvailability { RoomId = r.Id, DayOfWeek = c.DayOfWeek, SlotIndex = c.SlotIndex, State = AvailabilityState.Unavailable }));
        }
        await db.SaveChangesAsync(ct);
        await publisher.Publish(new EntityChangedNotification(user.InstitutionId, r.Target == AvailabilityTarget.Instructor ? "instructorAvailability" : "roomAvailability",
            r.Id, ChangeAction.Updated, AffectsSchedules: true), ct);
        return new AvailabilityDto(r.Target, r.Id, await LoadAsync(r.Target, r.Id, ct));
    }

    private Task<bool> ExistsAsync(AvailabilityTarget t, Guid id, CancellationToken ct) =>
        t == AvailabilityTarget.Instructor ? db.Instructors.AnyAsync(x => x.Id == id, ct) : db.Rooms.AnyAsync(x => x.Id == id, ct);

    private async Task<IReadOnlyList<AvailabilityCell>> LoadAsync(AvailabilityTarget t, Guid id, CancellationToken ct) =>
        t == AvailabilityTarget.Instructor
            ? await db.InstructorAvailabilities.AsNoTracking().Where(a => a.InstructorId == id).OrderBy(a => a.DayOfWeek).ThenBy(a => a.SlotIndex)
                .Select(a => new AvailabilityCell(a.DayOfWeek, a.SlotIndex, a.State)).ToListAsync(ct)
            : await db.RoomAvailabilities.AsNoTracking().Where(a => a.RoomId == id).OrderBy(a => a.DayOfWeek).ThenBy(a => a.SlotIndex)
                .Select(a => new AvailabilityCell(a.DayOfWeek, a.SlotIndex, a.State)).ToListAsync(ct);
}

/// <summary>Bumps the master-data version so cached schedule problems are rebuilt.</summary>
internal sealed class DataVersionBumper(ConfigVersion version) : INotificationHandler<EntityChangedNotification>
{
    public Task Handle(EntityChangedNotification n, CancellationToken ct)
    {
        version.BumpData(n.InstitutionId);
        return Task.CompletedTask;
    }
}
