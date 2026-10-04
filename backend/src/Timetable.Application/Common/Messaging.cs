using MediatR;
using Timetable.Domain.Common;

namespace Timetable.Application.Common;

/// <summary>Marker for state-changing requests (wrapped in a transaction by <c>TransactionBehavior</c>).</summary>
public interface ICommand<TResponse> : IRequest<TResponse>;

public interface IQuery<TResponse> : IRequest<TResponse>;

/// <summary>Request whose handler is only allowed with the given permission.</summary>
public interface IRequirePermission
{
    string RequiredPermission { get; }
}

public static class ResultFactory
{
    /// <summary>Creates a failed Result/Result&lt;T&gt; instance for an arbitrary TResponse.</summary>
    public static TResponse Fail<TResponse>(Error error)
    {
        var t = typeof(TResponse);
        if (t == typeof(Result)) return (TResponse)(object)Result.Failure(error);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var m = t.GetMethod(nameof(Result<object>.Failure), [typeof(Error)])!;
            return (TResponse)m.Invoke(null, [error])!;
        }
        throw new DomainException(error.Code);
    }
}
