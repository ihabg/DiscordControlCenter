using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.Services;

public interface IMessageDeliveryDialogService
{
    Task<MessageDeliveryResult?> PreviewConfirmAndDeliverAsync(
        MessageDraft draft,
        MessageOperationKind kind,
        string botDisplayName,
        CancellationToken cancellationToken);
}

public sealed class MessageDeliveryDialogService(
    IMessagePlanBuilder planner,
    IMessageDeliveryExecutor executor) : IMessageDeliveryDialogService
{
    public async Task<MessageDeliveryResult?> PreviewConfirmAndDeliverAsync(
        MessageDraft draft,
        MessageOperationKind kind,
        string botDisplayName,
        CancellationToken cancellationToken)
    {
        var planned = planner.Build(draft, kind);
        if (planned.Plan is null)
        {
            return null;
        }

        var preview = planner.BuildPreview(planned.Plan, botDisplayName);
        var window = new MessageConfirmationWindow(new MessageConfirmationViewModel(preview))
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return null;
        }

        return await executor.DeliverAsync(planned.Plan, cancellationToken).ConfigureAwait(true);
    }
}
