using System.Windows;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.Services;

public interface IOperationPlanSubmissionService
{
    Task<bool> ConfirmAndQueueAsync(
        OperationPlan plan,
        OperationPreview preview,
        CancellationToken cancellationToken);
}

public sealed class OperationPlanSubmissionService(
    IChannelOperationScheduler scheduler) : IOperationPlanSubmissionService
{
    public async Task<bool> ConfirmAndQueueAsync(
        OperationPlan plan,
        OperationPreview preview,
        CancellationToken cancellationToken)
    {
        var window = new OperationConfirmationWindow(
            new OperationConfirmationViewModel(plan, preview))
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var submission = await scheduler.EnqueueAsync(plan, cancellationToken).ConfigureAwait(true);
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
            $"Replacement operation queued. Correlation ID: {plan.CorrelationId}",
            "Operation queued",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return true;
    }
}
