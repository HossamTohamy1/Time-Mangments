using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;

namespace Timetable.Application.Common.Behaviors;

public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var sw = Stopwatch.StartNew();
        var response = await next(cancellationToken);
        sw.Stop();
        if (response is Result { IsFailure: true } r)
            logger.LogInformation("{Request} failed with {Code} in {Elapsed} ms", name, r.Error!.Code, sw.ElapsedMilliseconds);
        else if (sw.ElapsedMilliseconds > 500)
            logger.LogWarning("{Request} slow: {Elapsed} ms", name, sw.ElapsedMilliseconds);
        else
            logger.LogDebug("{Request} handled in {Elapsed} ms", name, sw.ElapsedMilliseconds);
        return response;
    }
}

public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser user) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is IRequirePermission p && !string.IsNullOrEmpty(p.RequiredPermission) && !user.HasPermission(p.RequiredPermission))
            return Task.FromResult(ResultFactory.Fail<TResponse>(Error.Forbidden("PERMISSION_REQUIRED")));
        return next(cancellationToken);
    }
}

/// <summary>Runs FluentValidation validators for the request and returns a VALIDATION_FAILED result with per-field errors.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!validators.Any()) return await next(cancellationToken);
        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, cancellationToken)));
        var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToList();
        if (failures.Count == 0) return await next(cancellationToken);

        var errors = failures
            .GroupBy(f => ToCamel(f.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(f => new FieldError(f.ErrorCode, f.ErrorMessage, f.FormattedMessagePlaceholderValues?
                .Where(kv => kv.Key is not "PropertyName" and not "PropertyValue" and not "PropertyPath")
                .ToDictionary(kv => kv.Key, kv => kv.Value))).ToArray());
        return ResultFactory.Fail<TResponse>(Error.Validation("VALIDATION_FAILED", details: errors));
    }

    private static string ToCamel(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var parts = s.Split('.');
        return string.Join('.', parts.Select(p => p.Length > 0 ? char.ToLowerInvariant(p[0]) + p[1..] : p));
    }
}

public sealed record FieldError(string Code, string Message, IReadOnlyDictionary<string, object>? Params);

/// <summary>Wraps commands in a database transaction (rolled back when the result is a failure).</summary>
public sealed class TransactionBehavior<TRequest, TResponse>(IAppDbContext db) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var isCommand = typeof(TRequest).GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));
        if (!isCommand || db.Database.CurrentTransaction is not null) return await next(cancellationToken);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var response = await next(cancellationToken);
            if (response is Result { IsFailure: true })
                await tx.RollbackAsync(cancellationToken);
            else
                await tx.CommitAsync(cancellationToken);
            return response;
        });
    }
}
