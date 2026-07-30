namespace DiscordControlCenter.Core.Bots;

public interface IBotProfileRepository
{
    Task<IReadOnlyList<BotProfile>> GetAllAsync(CancellationToken cancellationToken);
    Task<BotProfile?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(BotProfile profile, CancellationToken cancellationToken);
    Task UpdateAsync(BotProfile profile, CancellationToken cancellationToken);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken);
}
