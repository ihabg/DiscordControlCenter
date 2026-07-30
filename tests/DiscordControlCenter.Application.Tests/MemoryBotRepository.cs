using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.Application.Tests;

internal sealed class MemoryBotRepository(params BotProfile[] profiles) : IBotProfileRepository
{
    public List<BotProfile> Profiles { get; } = [.. profiles];

    public Task<IReadOnlyList<BotProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BotProfile>>(Profiles.ToArray());
    }

    public Task<BotProfile?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Profiles.FirstOrDefault(profile => profile.Id == id));
    }

    public Task AddAsync(BotProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Profiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(BotProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Profiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Profiles.RemoveAll(profile => profile.Id == id);
        return Task.CompletedTask;
    }
}
