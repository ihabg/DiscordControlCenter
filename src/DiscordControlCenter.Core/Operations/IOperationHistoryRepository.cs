namespace DiscordControlCenter.Core.Operations;

public interface IOperationHistoryRepository
{
    Task AddAsync(
        OperationHistoryEntry entry,
        CancellationToken cancellationToken);

    Task UpdateAsync(
        OperationHistoryEntry entry,
        CancellationToken cancellationToken);

    Task<OperationHistoryEntry?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationHistoryEntry>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationHistoryEntry>> GetInterruptedAsync(
        CancellationToken cancellationToken);
}

public interface IOperationBackupRepository
{
    Task SaveAsync(
        ServerStructureBackup backup,
        CancellationToken cancellationToken);

    Task<ServerStructureBackup?> GetAsync(
        string backupIdentifier,
        CancellationToken cancellationToken);
}
