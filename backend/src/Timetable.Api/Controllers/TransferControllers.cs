using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Api.Security;
using Timetable.Application.Features.Exports;
using Timetable.Application.Features.Imports;
using Timetable.Domain.Configuration;

namespace Timetable.Api.Controllers;

public sealed partial class SchedulesController
{
    /// <summary>
    /// Timetable as PDF (A4 landscape, Arabic right-to-left), Excel (sheet per resource + entries) or CSV (entries).
    /// view = group | instructor | room; ids = resources (empty = all with sessions); week = cycle index (empty = all weeks).
    /// </summary>
    [HttpGet("{id:guid}/export")]
    [RequiresFeature(FeatureCodes.ImportExport)]
    [ProducesResponseType<FileContentResult>(200)]
    public async Task<IActionResult> Export(Guid id, [FromQuery] string format = "pdf", [FromQuery] string view = "group", [FromQuery] Guid[]? ids = null,
        [FromQuery] string? lang = null, [FromQuery] int? week = null, CancellationToken ct = default)
    {
        var r = await Sender.Send(new ExportTimetableQuery(id, format, view, ids, lang, week), ct);
        return r.IsSuccess ? File(r.Value.Content, r.Value.ContentType, r.Value.FileName) : this.ToProblemResult(r.Error!);
    }
}

[Route("api/v{version:apiVersion}/imports")]
[RequiresFeature(FeatureCodes.ImportExport)]
public sealed class ImportsController : ApiControllerBase
{
    private const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Importable entity kinds and their columns (including active custom fields as cf.&lt;key&gt;).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ImportKindDto>>(200)]
    public async Task<IActionResult> Kinds(CancellationToken ct) => this.ToActionResult(await Sender.Send(new ListImportKindsQuery(), ct));

    [HttpGet("{kind}/template")]
    [ProducesResponseType<FileContentResult>(200)]
    public async Task<IActionResult> Template(string kind, [FromQuery] string format = "xlsx", CancellationToken ct = default)
    {
        var r = await Sender.Send(new ImportTemplateQuery(kind, format), ct);
        return r.IsSuccess ? File(r.Value.Content, r.Value.ContentType, r.Value.FileName) : this.ToProblemResult(r.Error!);
    }

    /// <summary>
    /// Imports an .xlsx/.csv file. dryRun=true validates everything (inside a rolled-back transaction) and returns the report;
    /// otherwise all rows are applied atomically, or none when any row has errors. mode = upsert (match by code) | insert (skip existing).
    /// </summary>
    [HttpPost("{kind}")]
    [RequestSizeLimit(MaxBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ImportReportDto>(200)]
    public async Task<IActionResult> Import(string kind, IFormFile file, [FromQuery] string mode = "upsert", [FromQuery] bool dryRun = true, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) return this.ToProblemResult(Domain.Common.Error.Validation("IMPORT_EMPTY"));
        if (file.Length > MaxBytes) return this.ToProblemResult(Domain.Common.Error.Validation("IMPORT_TOO_LARGE"));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return this.ToActionResult(await Sender.Send(new ImportFileCommand(kind, ms.ToArray(), file.FileName, mode, dryRun), ct));
    }
}
