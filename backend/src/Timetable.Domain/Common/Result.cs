namespace Timetable.Domain.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Concurrency,
    Unprocessable,
}

/// <summary>Expected failure with a stable machine-readable code, localizable message key and parameters.</summary>
public sealed record Error(string Code, ErrorKind Kind, IReadOnlyDictionary<string, object?>? Params = null, object? Details = null)
{
    public static Error NotFound(string entity, object? id = null) =>
        new("NOT_FOUND", ErrorKind.NotFound, new Dictionary<string, object?> { ["entity"] = entity, ["id"] = id?.ToString() });

    public static Error Validation(string code, IReadOnlyDictionary<string, object?>? p = null, object? details = null) =>
        new(code, ErrorKind.Validation, p, details);

    public static Error Conflict(string code, IReadOnlyDictionary<string, object?>? p = null, object? details = null) =>
        new(code, ErrorKind.Conflict, p, details);

    public static Error Forbidden(string code = "FORBIDDEN") => new(code, ErrorKind.Forbidden);

    public static Error Concurrency() => new("CONCURRENCY_CONFLICT", ErrorKind.Concurrency);
}

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(null);
    public static Result Failure(Error error) => new(error);
    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error) : base(error) => _value = value;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result has no value: {Error?.Code}");

    public static Result<T> Ok(T value) => new(value, null);
    public static new Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}

/// <summary>Thrown only for programming errors / broken invariants (unexpected failures).</summary>
public sealed class DomainException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}
