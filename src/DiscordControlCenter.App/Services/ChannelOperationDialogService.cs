using System.Windows;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Operations;

namespace DiscordControlCenter.App.Services;

public sealed class ChannelOperationDialogService(
    IChannelOperationPlanner planner,
    IChannelOperationScheduler scheduler) : IChannelOperationDialogService
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
        var confirmationViewModel = new OperationConfirmationViewModel(plan, preview);
        var confirmationWindow = new OperationConfirmationWindow(confirmationViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (confirmationWindow.ShowDialog() != true)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var submission = await scheduler
            .EnqueueAsync(plan, cancellationToken)
            .ConfigureAwait(true);
        if (!submission.Accepted)
        {
            MessageBox.Show(
                submission.Error ?? "The operation could not be queued.",
                "Operation not queued",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        MessageBox.Show(
            $"Operation queued. Position: {submission.QueuePosition ?? 1}.\n"
            + $"Correlation ID: {plan.CorrelationId}",
            "Operation queued",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return true;
    }
}
