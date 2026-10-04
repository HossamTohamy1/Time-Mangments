using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Timetable.Application.Abstractions;

namespace Timetable.Api.Security;

/// <summary>Endpoint requires a permission (policies check permissions, never role names).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(PermissionPolicyProvider.Prefix + permission);

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Creates "perm:x" (any of "a|b") policies on demand.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        return new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..])).Build();
    }
}

public sealed class PermissionHandler(ICurrentUser user) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.Permission.Split('|').Any(user.HasPermission)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
