using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Configuration;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Organization;

namespace Timetable.Application.Features.Institutions;

/// <summary>Creates an institution from a template (or blank) and grants the creator the ADMIN role.</summary>
public sealed class InstitutionProvisioner(IAppDbContext db, ITenantContext tenant, TemplateApplier applier)
{
    public async Task<Result<Institution>> CreateAsync(string code, string? nameAr, string? nameEn, string templateCode, string? defaultLanguage,
        Guid? adminUserId, CancellationToken ct)
    {
        using var _ = tenant.Bypass();
        code = code.Trim().ToUpperInvariant();
        if (await db.Institutions.AnyAsync(i => i.Code == code, ct))
            return Error.Conflict("CODE_ALREADY_EXISTS", new Dictionary<string, object?> { ["code"] = code });
        var template = await db.InstitutionTemplates.FirstOrDefaultAsync(t => t.Code == templateCode, ct);
        if (template is null) return Error.NotFound("template", templateCode);
        var bundle = TemplateBundle.Parse(template.BundleJson);

        var inst = new Institution
        {
            Code = code, NameAr = nameAr, NameEn = nameEn, TemplateCode = template.Code,
            DefaultLanguage = defaultLanguage ?? bundle.DefaultLanguage ?? "en",
        };
        db.Institutions.Add(inst);
        await db.SaveChangesAsync(ct);
        await applier.ApplyAsync(inst.Id, bundle, dryRun: false, ct);

        if (adminUserId is { } uid)
        {
            var admin = await db.AppRoles.FirstAsync(r => r.InstitutionId == inst.Id && r.Code == "ADMIN", ct);
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = uid, InstitutionId = inst.Id, RoleId = admin.Id });
            await db.SaveChangesAsync(ct);
        }
        return inst;
    }
}
