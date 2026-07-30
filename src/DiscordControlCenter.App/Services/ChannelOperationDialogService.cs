using System.Windows;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Operations;

namespace DiscordControlCenter.App.Services;

public sealed class ChannelOperationDialogService(
    IChannelOperationPlanner planner,
    IOperationPlanSubmissionService submissionService) : IChannelOperationDialogService
{
    public async Task<bool> ConfigurePreviewConfirmAndQueueAsync(
        ChannelOperationContext context,
        ChannelOperationUiMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draftViewModel = new ChannelOperationDraftViewModel(context, mode, planner);
        var draftWindow = new ChannelOperationDraftWindow(draftViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (draftWindow.ShowDialog() != true || draftWindow.Plan is not { } plan)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preview = planner.BuildPreview(plan, context.BotDisplayName);
        return await submissionService
            .ConfirmAndQueueAsync(plan, preview, cancellationToken)
            .ConfigureAwait(true);
    }
}
