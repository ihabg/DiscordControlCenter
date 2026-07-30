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

public interface IOperationHistoryQueryRepository
{
    Task<PagedResult<OperationHistoryEntry>> QueryAsync(
        OperationHistoryQuery query,
        CancellationToken cancellationToken);

    Task<OperationHistoryDetail?> GetDetailAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task AddTransitionAsync(
        OperationStateTransition transition,
        CancellationToken cancellationToken);

    Task AddManualDecisionAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken);
}

public interface IBackupCatalogRepository
{
    Task<PagedResult<BackupCatalogItem>> QueryAsync(
        BackupQuery query,
        CancellationToken cancellationToken);

    Task<BackupCatalogItem?> GetCatalogItemAsync(
        string backupIdentifier,
        CancellationToken cancellationToken);

    Task SetPinnedAsync(
        string backupIdentifier,
        bool isPinned,
        CancellationToken cancellationToken);

    Task DeleteLocalAsync(
        IReadOnlyCollection<string> backupIdentifiers,
        string safeReason,
        CancellationToken cancellationToken);

    Task<BackupRetentionPolicy> GetRetentionPolicyAsync(CancellationToken cancellationToken);

    Task SaveRetentionPolicyAsync(
        BackupRetentionPolicy policy,
        CancellationToken cancellationToken);

    Task<BackupCleanupPreview> PreviewCleanupAsync(
        BackupRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IManualReconciliationRepository
{
    Task AddAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken);
}
