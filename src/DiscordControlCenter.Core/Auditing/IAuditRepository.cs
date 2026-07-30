namespace DiscordControlCenter.Core.Auditing;

public interface IAuditRepository
{
    Task AddAsync(AuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count, CancellationToken cancellationToken);
}
