using MediatR;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Domain.Common;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Users;

public sealed record ListUsersQuery(string? Search) : IQuery<Result<IReadOnlyList<UserListItemDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.UsersManage;
}

public sealed record UpsertUserCommand(Guid? Id, UpsertUserInput Input) : ICommand<Result<Guid>>, IRequirePermission
{
    public string RequiredPermission => Permissions.UsersManage;
}

internal sealed class UserHandlers(IIdentityService identity, ICurrentUser user) :
    IRequestHandler<ListUsersQuery, Result<IReadOnlyList<UserListItemDto>>>,
    IRequestHandler<UpsertUserCommand, Result<Guid>>
{
    public async Task<Result<IReadOnlyList<UserListItemDto>>> Handle(ListUsersQuery r, CancellationToken ct) =>
        Result<IReadOnlyList<UserListItemDto>>.Ok(await identity.ListUsersAsync(user.InstitutionId, r.Search, ct));

    public async Task<Result<Guid>> Handle(UpsertUserCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Input.Email) || !r.Input.Email.Contains('@', StringComparison.Ordinal))
            return Common.Behaviors.ValidationErrors.Single("email", "EMAIL_INVALID");
        if (r.Id == user.UserId && !r.Input.IsActive) return Error.Validation("CANNOT_DEACTIVATE_SELF");
        return await identity.UpsertUserAsync(user.InstitutionId, r.Id, r.Input, ct);
    }
}
