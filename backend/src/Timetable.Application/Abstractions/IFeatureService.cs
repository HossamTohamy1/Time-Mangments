namespace Timetable.Application.Abstractions;

/// <summary>Per-institution feature flags (read from the cached effective configuration).</summary>
public interface IFeatureService
{
    Task<bool> IsEnabledAsync(string feature, CancellationToken ct = default);
}
