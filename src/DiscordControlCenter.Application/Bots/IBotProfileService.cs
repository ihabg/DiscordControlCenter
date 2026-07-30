using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;

namespace DiscordControlCenter.Application.Bots;

public interface IBotProfileService
{
    Task<IReadOnlyList<BotProfile>> GetAllAsync(CancellationToken cancellationToken);
    Task<OperationResult<BotProfile>> AddAsync(AddBotRequest request, CancellationToken cancellationToken);
    Task<OperationResult<BotProfile>> ReplaceTokenAsync(
        Guid botProfileId,
        string newToken,
        CancellationToken cancellationToken);
    Task<OperationResult<BotProfile>> SetFullMemberAccessAsync(
        Guid botProfileId,
        bool enabled,
        CancellationToken cancellationToken);
    Task<OperationResult> RemoveAsync(Guid botProfileId, CancellationToken cancellationToken);
}
