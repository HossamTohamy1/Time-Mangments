namespace Timetable.Application.Common;

public sealed record ListRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public bool Desc { get; init; }
    /// <summary>Simple equality filters (field → value), e.g. roomTypeId=..., includeInactive=true.</summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();

    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize switch { < 1 => 25, > 500 => 500, _ => PageSize };
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
