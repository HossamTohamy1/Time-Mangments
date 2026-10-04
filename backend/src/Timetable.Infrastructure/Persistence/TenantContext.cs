using Timetable.Application.Abstractions;

namespace Timetable.Infrastructure.Persistence;

/// <summary>Scoped tenant holder read by the DbContext query filters.</summary>
public sealed class TenantContext : ITenantContext
{
    private int _bypassDepth;

    public Guid InstitutionId { get; private set; }
    public bool BypassFilter => _bypassDepth > 0;

    public void Set(Guid institutionId) => InstitutionId = institutionId;

    public IDisposable Bypass()
    {
        _bypassDepth++;
        return new Releaser(this);
    }

    private sealed class Releaser(TenantContext owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._bypassDepth--;
        }
    }
}
