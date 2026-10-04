using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Features.Academic;
using Timetable.Application.Features.Availability;
using Timetable.Application.Features.Organization;
using Timetable.Application.Features.Resources;
using Timetable.Application.Features.Settings;
using Timetable.Domain.Academic;
using Timetable.Domain.Configuration;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;

namespace Timetable.Api.Controllers;

[Route("api/v{version:apiVersion}/buildings")]
public sealed class BuildingsController : CrudController<Building, BuildingDto, BuildingInput>;

[Route("api/v{version:apiVersion}/travel-times")]
public sealed class TravelTimesController : CrudController<BuildingTravelTime, TravelTimeDto, TravelTimeInput>;

[Route("api/v{version:apiVersion}/rooms")]
public sealed class RoomsController : CrudController<Room, RoomDto, RoomInput>
{
    [HttpGet("{id:guid}/availability")]
    [ProducesResponseType<AvailabilityDto>(200)]
    public async Task<IActionResult> GetAvailability(Guid id, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new GetAvailabilityQuery(AvailabilityTarget.Room, id), ct));

    [HttpPut("{id:guid}/availability")]
    public async Task<IActionResult> SaveAvailability(Guid id, [FromBody] IReadOnlyList<AvailabilityCell> cells, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveAvailabilityCommand(AvailabilityTarget.Room, id, cells), ct));
}

[Route("api/v{version:apiVersion}/instructors")]
public sealed class InstructorsController : CrudController<Instructor, InstructorDto, InstructorInput>
{
    [HttpGet("{id:guid}/availability")]
    [ProducesResponseType<AvailabilityDto>(200)]
    public async Task<IActionResult> GetAvailability(Guid id, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new GetAvailabilityQuery(AvailabilityTarget.Instructor, id), ct));

    [HttpPut("{id:guid}/availability")]
    public async Task<IActionResult> SaveAvailability(Guid id, [FromBody] IReadOnlyList<AvailabilityCell> cells, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveAvailabilityCommand(AvailabilityTarget.Instructor, id, cells), ct));
}

[Route("api/v{version:apiVersion}/org-units")]
public sealed class OrgUnitsController : CrudController<OrgUnit, OrgUnitDto, OrgUnitInput>;

[Route("api/v{version:apiVersion}/groups")]
public sealed class GroupsController : CrudController<StudentGroup, GroupDto, GroupInput>;

[Route("api/v{version:apiVersion}/courses")]
public sealed class CoursesController : CrudController<Course, CourseDto, CourseInput>;

[Route("api/v{version:apiVersion}/terms")]
public sealed class TermsController : CrudController<AcademicTerm, TermDto, TermInput>;

[Route("api/v{version:apiVersion}/sessions")]
public sealed class SessionsController : CrudController<Session, SessionDto, SessionInput>;

[Route("api/v{version:apiVersion}/curriculum-rules")]
public sealed class CurriculumRulesController : CrudController<CurriculumRule, CurriculumRuleDto, CurriculumRuleInput>;

[Route("api/v{version:apiVersion}/offerings")]
public sealed class OfferingsController : CrudController<CourseOffering, OfferingDto, OfferingInput>;

[Route("api/v{version:apiVersion}/custom-fields")]
public sealed class CustomFieldsController : CrudController<CustomFieldDefinition, Application.Features.Configuration.CustomFieldDefinitionDto, CustomFieldInput>;

[Route("api/v{version:apiVersion}/roles")]
public sealed class RolesController : CrudController<Role, RoleDto, RoleInput>;
