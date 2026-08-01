namespace DiscordControlCenter.Application.Bots;

/// <summary>Restricted exact-ID lookup used only by the internal teast acceptance runner.</summary>
public interface ILiveValidationMessageVerifier
{
    Task<bool> MessageExistsAsync(Guid botProfileId, ulong channelId, ulong messageId, CancellationToken cancellationToken);
}
