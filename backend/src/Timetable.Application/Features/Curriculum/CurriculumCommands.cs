using MediatR;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Curriculum;

/// <summary>Preview (apply = false) or apply the curriculum → sessions diff for a term (optionally one org-unit subtree).</summary>
public sealed record GenerateSessionsCommand(Guid TermId, Guid? OrgUnitId, bool Apply) : ICommand<Result<SessionGenerationResult>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class GenerateSessionsHandler(CurriculumSessionGenerator generator, IFeatureService features, ICurrentUser user, IPublisher publisher)
    : IRequestHandler<GenerateSessionsCommand, Result<SessionGenerationResult>>
{
    public async Task<Result<SessionGenerationResult>> Handle(GenerateSessionsCommand r, CancellationToken ct)
    {
        if (!await features.IsEnabledAsync(FeatureCodes.Curriculum, ct)) return Error.Forbidden("FEATURE_DISABLED");
        var result = await generator.GenerateAsync(user.InstitutionId, r.TermId, r.OrgUnitId, r.Apply, ct);
        if (r.Apply) await publisher.Publish(new EntityChangedNotification(user.InstitutionId, "session", r.TermId, ChangeAction.Updated, true), ct);
        return result;
    }
}
