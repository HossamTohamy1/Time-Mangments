using Microsoft.AspNetCore.Mvc;
using Timetable.Application.Features.Settings;
using Timetable.Domain.Configuration;

namespace Timetable.Api.Controllers;

/// <summary>No-code Rule Builder rules (preview / impact endpoints are added with the validator).</summary>
[Route("api/v{version:apiVersion}/rules")]
public sealed partial class RulesController : CrudController<RuleDefinition, RuleDto, RuleInput>;
