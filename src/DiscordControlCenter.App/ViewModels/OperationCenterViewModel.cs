using System.Collections.ObjectModel;
using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.ViewModels;

public sealed class OperationCenterViewModel : ObservableObject, IDisposable
{
    private readonly IChannelOperationScheduler _scheduler;
    private readonly UiDispatcher _dispatcher;
    private OperationItemViewModel? _selectedOperation;
    private bool _disposed;

    public OperationCenterViewModel(
        IChannelOperationScheduler scheduler,
        UiDispatcher dispatcher)
    {
        _scheduler = scheduler;
        _dispatcher = dispatcher;
        CancelCommand = new RelayCommand(_ => CancelSelected(), _ => CanCancelSelected);
        RegeneratePreviewCommand = new RelayCommand(
            _ => RegeneratePreviewRequested?.Invoke(this, EventArgs.Empty),
            _ => CanRegeneratePreview);
        foreach (var snapshot in scheduler.Snapshots)
        {
            Operations.Add(new OperationItemViewModel(snapshot));
        }

        SelectedOperation = Operations.Count == 0 ? null : Operations[0];
        _scheduler.OperationChanged += OnOperationChanged;
    }

    public event EventHandler? RegeneratePreviewRequested;

    public ObservableCollection<OperationItemViewModel> Operations { get; } = [];

    public OperationItemViewModel? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (SetProperty(ref _selectedOperation, value))
            {
                NotifySelectionChanged();
            }
        }
    }

    public bool HasOperations => Operations.Count > 0;
    public bool HasSelection => SelectedOperation is not null;
    public bool CanCancelSelected => SelectedOperation?.CanCancel == true;
    public bool CanRegeneratePreview => SelectedOperation?.CanRegeneratePreview == true;
    public string EmptyTitle { get; } = "No channel operations yet";
    public string EmptyMessage { get; } =
        "Confirmed channel changes and their persisted results will appear here.";
    public RelayCommand CancelCommand { get; }
    public RelayCommand RegeneratePreviewCommand { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.OperationChanged -= OnOperationChanged;
    }

    private void CancelSelected()
    {
        if (SelectedOperation is { } selected
            && _scheduler.Cancel(selected.OperationId))
        {
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnOperationChanged(object? sender, QueuedOperationSnapshot snapshot)
    {
        _ = sender;
        _dispatcher.Post(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(QueuedOperationSnapshot snapshot)
    {
        OperationItemViewModel? item = null;
        foreach (var candidate in Operations)
        {
            if (candidate.OperationId == snapshot.Plan.OperationId)
            {
                item = candidate;
                break;
            }
        }

        if (item is null)
        {
            item = new OperationItemViewModel(snapshot);
            Operations.Insert(0, item);
            SelectedOperation ??= item;
            OnPropertyChanged(nameof(HasOperations));
        }
        else
        {
            item.Update(snapshot);
        }

        if (ReferenceEquals(SelectedOperation, item))
        {
            NotifySelectionChanged();
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCancelSelected));
        OnPropertyChanged(nameof(CanRegeneratePreview));
        CancelCommand.NotifyCanExecuteChanged();
        RegeneratePreviewCommand.NotifyCanExecuteChanged();
    }
}

public sealed class OperationItemViewModel : ObservableObject
{
    private QueuedOperationSnapshot _snapshot;

    public OperationItemViewModel(QueuedOperationSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public Guid OperationId => _snapshot.Plan.OperationId;
    public string Title => _snapshot.Plan.Title;
    public string OperationTypeText => SplitWords(_snapshot.Plan.OperationType.ToString());
    public string ServerName => _snapshot.Plan.ServerNameSnapshot;
    public string BotProfileIdText => _snapshot.Plan.BotProfileId.ToString();
    public string AuditReasonText => string.IsNullOrWhiteSpace(_snapshot.Plan.AuditReason)
        ? "Not supplied"
        : _snapshot.Plan.AuditReason;
    public string RiskText => $"{_snapshot.Plan.RiskLevel} risk";
    public string StateText => SplitWords(_snapshot.State.ToString());
    public string StatusSummary => _snapshot.Progress?.Message
        ?? _snapshot.Result?.Failure?.SafeMessage
        ?? StateText;
    public string CorrelationIdText => _snapshot.Plan.CorrelationId.ToString();
    public string CreatedAtText =>
        _snapshot.Plan.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string StartedAtText => _snapshot.Result?.StartedAt.ToLocalTime()
        .ToString("g", CultureInfo.CurrentCulture) ?? "Not started";
    public string FinishedAtText => _snapshot.Result?.FinishedAt.ToLocalTime()
        .ToString("g", CultureInfo.CurrentCulture) ?? "Not finished";
    public string DurationText => _snapshot.Result is { } result
        ? FormatDuration(result.FinishedAt - result.StartedAt)
        : "In progress";
    public string TargetIdsText => _snapshot.Plan.ExactTargetIds.Length == 0
        ? "New resources"
        : string.Join(", ", _snapshot.Plan.ExactTargetIds);
    public string RequestCountText =>
        _snapshot.Plan.EstimatedRequestCount.ToString(CultureInfo.CurrentCulture);
    public string ProgressText
    {
        get
        {
            var completed = _snapshot.Progress?.CompletedSteps
                ?? _snapshot.Result?.CompletedCount
                ?? 0;
            return $"{completed} / {_snapshot.Plan.Steps.Length} steps";
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (_snapshot.Plan.Steps.Length == 0)
            {
                return 0;
            }

            var completed = _snapshot.Progress?.CompletedSteps
                ?? _snapshot.Result?.CompletedCount
                ?? 0;
            return Math.Clamp(completed * 100d / _snapshot.Plan.Steps.Length, 0, 100);
        }
    }

    public string CountsText => _snapshot.Result is { } result
        ? $"{result.CompletedCount} completed, {result.FailedCount} failed, "
          + $"{result.CancelledCount} cancelled"
        : ProgressText;
    public string ErrorCodeText => _snapshot.Result?.Failure?.SafeCode ?? "None";
    public string ExceptionTypeText => _snapshot.Result?.Failure?.ExceptionType ?? "None";
    public string OutcomeCertaintyText =>
        _snapshot.Result?.Failure?.OutcomeCertainty.ToString() ?? "Known";
    public string ReconciliationText => _snapshot.Result?.Reconciliation.SafeSummary
        ?? "Final reconciliation has not run yet.";
    public string CompensationText => _snapshot.Result?.CompensationSummary
        ?? SplitWords(_snapshot.Plan.CompensationCapability.ToString());
    public string BackupText => _snapshot.Result?.BackupIdentifier
        ?? (_snapshot.Plan.IsDestructive ? "Pending required backup" : "Not required");
    public IReadOnlyList<OperationStepResult> StepResults =>
        _snapshot.Result?.StepResults ?? [];
    public bool CanCancel => _snapshot.State is ChannelOperationState.Pending
        or ChannelOperationState.Running
        or ChannelOperationState.Cancelling;
    public bool CanRegeneratePreview => _snapshot.State is ChannelOperationState.Failed
        or ChannelOperationState.Stale
        or ChannelOperationState.PartiallyCompleted
        or ChannelOperationState.ReconciliationRequired
        or ChannelOperationState.Cancelled;
    public string RetryGuidance => CanRegeneratePreview
        ? "A failed or stale plan is never replayed blindly. Return to Channel Explorer, "
          + "refresh current state, generate a new preview, and confirm again."
        : "Retry becomes available only after a terminal result that is safe to re-plan.";

    public void Update(QueuedOperationSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty);
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds:0} ms"
            : $"{duration.TotalSeconds:0.0} s";

    private static string SplitWords(string value) =>
        string.Concat(
            value.Select(
                (character, index) =>
                    index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
