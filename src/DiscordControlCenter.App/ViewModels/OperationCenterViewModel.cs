using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.ViewModels;

public sealed class OperationCenterViewModel : ObservableObject, IDisposable
{
    private readonly IChannelOperationScheduler _scheduler;
    private readonly UiDispatcher _dispatcher;
    private readonly IOperationHistoryQueryRepository? _history;
    private readonly IOperationRecoveryService? _recovery;
    private readonly IOperationExportService? _export;
    private OperationItemViewModel? _selectedOperation;
    private string _searchText = string.Empty;
    private int _historyPage = 1;
    private int _historyPages;
    private string? _historyError;
    private OperationHistoryDetail? _selectedHistoryDetail;
    private string _botFilter = string.Empty;
    private string _serverFilter = string.Empty;
    private string _createdFromFilter = string.Empty;
    private string _createdToFilter = string.Empty;
    private string _operationTypeFilter = "All";
    private string _stateFilter = "All";
    private string _riskFilter = "All";
    private string _backupFilter = "All";
    private string _manualFilter = "All";
    private OperationHistorySort _selectedSort = OperationHistorySort.Newest;
    private ManualReconciliationResolution _selectedResolution =
        ManualReconciliationResolution.KeepCurrentStateAndStop;
    private OperationStepResult? _selectedReconciliationStep;
    private string _reconciliationExplanation = string.Empty;
    private string _relevantResourceIds = string.Empty;
    private CancellationTokenSource? _detailCancellation;
    private bool _disposed;

    public OperationCenterViewModel(
        IChannelOperationScheduler scheduler,
        UiDispatcher dispatcher,
        IOperationHistoryQueryRepository? history = null,
        IOperationRecoveryService? recovery = null,
        IOperationExportService? export = null)
    {
        _scheduler = scheduler;
        _dispatcher = dispatcher;
        _history = history;
        _recovery = recovery;
        _export = export;
        CancelCommand = new RelayCommand(_ => CancelSelected(), _ => CanCancelSelected);
        RegeneratePreviewCommand = new RelayCommand(
            _ => RegeneratePreviewRequested?.Invoke(this, EventArgs.Empty),
            _ => CanRegeneratePreview);
        SearchCommand = new AsyncRelayCommand(
            NewSearchAsync,
            () => _history is not null,
            HandleCommandError);
        NextHistoryPageCommand = new AsyncRelayCommand(
            NextHistoryPageAsync,
            () => HistoryPage < HistoryPages,
            HandleCommandError);
        PreviousHistoryPageCommand = new AsyncRelayCommand(
            PreviousHistoryPageAsync,
            () => HistoryPage > 1,
            HandleCommandError);
        ArchiveAmbiguityCommand = new AsyncRelayCommand(
            ArchiveAmbiguityAsync,
            () => SelectedOperation?.State == ChannelOperationState.ReconciliationRequired
                  && _recovery is not null,
            HandleCommandError);
        RecordDecisionCommand = new AsyncRelayCommand(
            RecordDecisionAsync,
            () => SelectedOperation?.State == ChannelOperationState.ReconciliationRequired
                  && SelectedReconciliationStep is not null
                  && _recovery is not null,
            HandleCommandError);
        ExportJsonCommand = new AsyncRelayCommand(
            token => ExportAsync(false, token),
            () => _export is not null,
            HandleCommandError);
        ExportCsvCommand = new AsyncRelayCommand(
            token => ExportAsync(true, token),
            () => _export is not null,
            HandleCommandError);
        foreach (var snapshot in scheduler.Snapshots)
        {
            Operations.Add(new OperationItemViewModel(snapshot));
        }

        SelectedOperation = Operations.Count == 0 ? null : Operations[0];
        _scheduler.OperationChanged += OnOperationChanged;
    }

    public event EventHandler? RegeneratePreviewRequested;

    public ObservableCollection<OperationItemViewModel> Operations { get; } = [];
    public IReadOnlyList<string> OperationTypeOptions { get; } =
        ["All", .. Enum.GetNames<ChannelOperationType>()];
    public IReadOnlyList<string> StateOptions { get; } =
        ["All", .. Enum.GetNames<ChannelOperationState>()];
    public IReadOnlyList<string> RiskOptions { get; } =
        ["All", .. Enum.GetNames<OperationRiskLevel>()];
    public IReadOnlyList<string> BooleanFilterOptions { get; } = ["All", "Yes", "No"];
    public IReadOnlyList<OperationHistorySort> SortOptions { get; } =
        Enum.GetValues<OperationHistorySort>();
    public IReadOnlyList<ManualReconciliationResolution> ReconciliationResolutions { get; } =
        Enum.GetValues<ManualReconciliationResolution>();
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string BotFilter
    {
        get => _botFilter;
        set => SetProperty(ref _botFilter, value);
    }

    public string ServerFilter
    {
        get => _serverFilter;
        set => SetProperty(ref _serverFilter, value);
    }

    public string CreatedFromFilter
    {
        get => _createdFromFilter;
        set => SetProperty(ref _createdFromFilter, value);
    }

    public string CreatedToFilter
    {
        get => _createdToFilter;
        set => SetProperty(ref _createdToFilter, value);
    }

    public string OperationTypeFilter
    {
        get => _operationTypeFilter;
        set => SetProperty(ref _operationTypeFilter, value);
    }

    public string StateFilter
    {
        get => _stateFilter;
        set => SetProperty(ref _stateFilter, value);
    }

    public string RiskFilter
    {
        get => _riskFilter;
        set => SetProperty(ref _riskFilter, value);
    }

    public string BackupFilter
    {
        get => _backupFilter;
        set => SetProperty(ref _backupFilter, value);
    }

    public string ManualFilter
    {
        get => _manualFilter;
        set => SetProperty(ref _manualFilter, value);
    }

    public OperationHistorySort SelectedSort
    {
        get => _selectedSort;
        set => SetProperty(ref _selectedSort, value);
    }

    public int HistoryPage
    {
        get => _historyPage;
        private set => SetProperty(ref _historyPage, value);
    }

    public int HistoryPages
    {
        get => _historyPages;
        private set => SetProperty(ref _historyPages, value);
    }

    public string HistoryPageText => $"Page {HistoryPage} of {Math.Max(HistoryPages, 1)}";
    public string? HistoryError
    {
        get => _historyError;
        private set => SetProperty(ref _historyError, value);
    }

    public OperationHistoryDetail? SelectedHistoryDetail
    {
        get => _selectedHistoryDetail;
        private set
        {
            if (SetProperty(ref _selectedHistoryDetail, value))
            {
                OnPropertyChanged(nameof(HasTimeline));
                OnPropertyChanged(nameof(HasManualDecisions));
            }
        }
    }

    public bool HasTimeline => SelectedHistoryDetail?.Timeline.Length > 0;
    public bool HasManualDecisions => SelectedHistoryDetail?.ManualDecisions.Length > 0;
    public ManualReconciliationResolution SelectedResolution
    {
        get => _selectedResolution;
        set => SetProperty(ref _selectedResolution, value);
    }

    public OperationStepResult? SelectedReconciliationStep
    {
        get => _selectedReconciliationStep;
        set
        {
            if (SetProperty(ref _selectedReconciliationStep, value))
            {
                RecordDecisionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ReconciliationExplanation
    {
        get => _reconciliationExplanation;
        set => SetProperty(ref _reconciliationExplanation, value);
    }

    public string RelevantResourceIds
    {
        get => _relevantResourceIds;
        set => SetProperty(ref _relevantResourceIds, value);
    }

    public OperationItemViewModel? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (SetProperty(ref _selectedOperation, value))
            {
                NotifySelectionChanged();
                _ = LoadHistoryDetailAsync(value);
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
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand NextHistoryPageCommand { get; }
    public AsyncRelayCommand PreviousHistoryPageCommand { get; }
    public AsyncRelayCommand ArchiveAmbiguityCommand { get; }
    public AsyncRelayCommand RecordDecisionCommand { get; }
    public AsyncRelayCommand ExportJsonCommand { get; }
    public AsyncRelayCommand ExportCsvCommand { get; }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        SearchAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.OperationChanged -= OnOperationChanged;
        SearchCommand.Dispose();
        NextHistoryPageCommand.Dispose();
        PreviousHistoryPageCommand.Dispose();
        ArchiveAmbiguityCommand.Dispose();
        RecordDecisionCommand.Dispose();
        ExportJsonCommand.Dispose();
        ExportCsvCommand.Dispose();
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
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
        ArchiveAmbiguityCommand.NotifyCanExecuteChanged();
        RecordDecisionCommand.NotifyCanExecuteChanged();
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (_history is null)
        {
            return;
        }

        HistoryError = null;
        try
        {
            if (!TryBuildQuery(HistoryPage, 50, out var query, out var validationError))
            {
                HistoryError = validationError;
                return;
            }

            var page = await _history.QueryAsync(
                query!,
                cancellationToken);
            Operations.Clear();
            foreach (var entry in page.Items)
            {
                try
                {
                    var plan = JsonSerializer.Deserialize<OperationPlan>(entry.PlanJson);
                    var result = entry.ResultJson is null
                        ? null
                        : JsonSerializer.Deserialize<ChannelOperationResult>(entry.ResultJson);
                    if (plan is not null)
                    {
                        Operations.Add(
                            new OperationItemViewModel(
                                new QueuedOperationSnapshot(
                                    plan,
                                    entry.State,
                                    0,
                                    null,
                                    result,
                                    entry.CreatedAt)));
                    }
                }
                catch (JsonException)
                {
                    HistoryError = "One or more corrupt history records were omitted.";
                }
            }

            HistoryPages = page.TotalPages;
            SelectedOperation = Operations.FirstOrDefault();
            OnPropertyChanged(nameof(HasOperations));
            OnPropertyChanged(nameof(HistoryPageText));
            NotifyHistoryPaging();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HistoryError = "Persisted operation history could not be queried.";
        }
    }

    private async Task NewSearchAsync(CancellationToken cancellationToken)
    {
        HistoryPage = 1;
        await SearchAsync(cancellationToken);
    }

    private async Task NextHistoryPageAsync(CancellationToken cancellationToken)
    {
        HistoryPage++;
        await SearchAsync(cancellationToken);
    }

    private async Task PreviousHistoryPageAsync(CancellationToken cancellationToken)
    {
        HistoryPage--;
        await SearchAsync(cancellationToken);
    }

    private async Task ArchiveAmbiguityAsync(CancellationToken cancellationToken)
    {
        if (_recovery is null || SelectedOperation is not { } selected)
        {
            return;
        }

        await _recovery.RecordDecisionAsync(
            new ManualReconciliationDecision(
                0,
                selected.OperationId,
                selected.CorrelationId,
                selected.StepResults.Count == 0 ? Guid.Empty : selected.StepResults[0].StepId,
                ManualReconciliationResolution.ArchiveWithWarning,
                DateTimeOffset.UtcNow,
                "The user kept current state and archived the ambiguous result with a warning.",
                []),
            cancellationToken);
        await LoadHistoryDetailAsync(selected);
    }

    private async Task RecordDecisionAsync(CancellationToken cancellationToken)
    {
        if (_recovery is null
            || SelectedOperation is not { } operation
            || SelectedReconciliationStep is not { } step)
        {
            return;
        }

        if (!TryParseResourceIds(RelevantResourceIds, out var resourceIds))
        {
            HistoryError = "Relevant resource IDs must be comma-separated Discord numeric IDs.";
            return;
        }

        var explanation = string.IsNullOrWhiteSpace(ReconciliationExplanation)
            ? $"The user selected {SelectedResolution}; no Discord mutation was performed."
            : ReconciliationExplanation.Trim();
        await _recovery.RecordDecisionAsync(
            new ManualReconciliationDecision(
                0,
                operation.OperationId,
                operation.CorrelationId,
                step.StepId,
                SelectedResolution,
                DateTimeOffset.UtcNow,
                explanation,
                resourceIds),
            cancellationToken);
        ReconciliationExplanation = string.Empty;
        RelevantResourceIds = string.Empty;
        await LoadHistoryDetailAsync(operation);
    }

    private void NotifyHistoryPaging()
    {
        NextHistoryPageCommand.NotifyCanExecuteChanged();
        PreviousHistoryPageCommand.NotifyCanExecuteChanged();
    }

    private void HandleCommandError(Exception exception)
    {
        _ = exception;
        HistoryError =
            "The requested history action could not be completed. No Discord action was performed.";
    }

    private async Task ExportAsync(bool csv, CancellationToken cancellationToken)
    {
        if (_export is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = csv ? "Export safe operation history CSV" : "Export safe operation history JSON",
            Filter = csv ? "CSV files (*.csv)|*.csv" : "JSON files (*.json)|*.json",
            DefaultExt = csv ? ".csv" : ".json",
            FileName = csv ? "operation-history.csv" : "operation-history.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!TryBuildQuery(1, 100, out var query, out var validationError))
        {
            HistoryError = validationError;
            return;
        }

        var recordCount = _history is null
            ? 0
            : (await _history.QueryAsync(
                    query! with { PageSize = 1 },
                    cancellationToken))
                .TotalCount;
        if (MessageBox.Show(
                $"Export type: {(csv ? "operation-history CSV" : "versioned operation-history JSON")}\n"
                + $"Record count: {recordCount:N0}\n"
                + $"Destination: {dialog.FileName}\n\n"
                + "Included: IDs, server, state, risk, counts, timestamps, backup reference, safe codes, and audit reason.\n"
                + "Excluded: credentials, raw payloads, messages, private content, stack traces, and user paths.",
                "Confirm safe export",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        await using var stream = new FileStream(
            dialog.FileName,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        if (csv)
        {
            await _export.ExportHistoryCsvAsync(stream, query!, cancellationToken);
        }
        else
        {
            await _export.ExportHistoryJsonAsync(stream, query!, cancellationToken);
        }
    }

    private async Task LoadHistoryDetailAsync(OperationItemViewModel? operation)
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        var token = _detailCancellation.Token;
        SelectedHistoryDetail = null;
        var steps = operation?.StepResults;
        SelectedReconciliationStep = steps?.FirstOrDefault(step => !step.Succeeded)
            ?? (steps is { Count: > 0 } ? steps[0] : null);
        if (_history is null || operation is null)
        {
            return;
        }

        try
        {
            SelectedHistoryDetail = await _history.GetDetailAsync(operation.OperationId, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            HistoryError = "The selected operation detail could not be loaded safely.";
        }
    }

    private bool TryBuildQuery(
        int pageNumber,
        int pageSize,
        out OperationHistoryQuery? query,
        out string? error)
    {
        query = null;
        error = null;
        if (!TryOptionalGuid(BotFilter, out var botId)
            || !TryOptionalUlong(ServerFilter, out var serverId)
            || !TryOptionalDate(CreatedFromFilter, out var from)
            || !TryOptionalDate(CreatedToFilter, out var to))
        {
            error = "Enter valid bot/server IDs and dates, or leave those filters blank.";
            return false;
        }

        query = new OperationHistoryQuery(
            SearchText,
            botId,
            serverId,
            Enum.TryParse<ChannelOperationType>(OperationTypeFilter, out var operationType)
                ? operationType
                : null,
            Enum.TryParse<ChannelOperationState>(StateFilter, out var state) ? state : null,
            Enum.TryParse<OperationRiskLevel>(RiskFilter, out var risk) ? risk : null,
            from,
            to,
            ParseBooleanFilter(BackupFilter),
            ParseBooleanFilter(ManualFilter),
            SelectedSort,
            pageNumber,
            pageSize);
        return true;
    }

    private static bool? ParseBooleanFilter(string value) =>
        value switch
        {
            "Yes" => true,
            "No" => false,
            _ => null
        };

    private static bool TryOptionalGuid(string value, out Guid? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Guid.TryParse(value.Trim(), out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static bool TryOptionalUlong(string value, out ulong? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static bool TryOptionalDate(string value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.CurrentCulture, out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static bool TryParseResourceIds(
        string value,
        out ImmutableArray<ulong> resourceIds)
    {
        var builder = ImmutableArray.CreateBuilder<ulong>();
        foreach (var item in value.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ulong.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                resourceIds = [];
                return false;
            }

            builder.Add(id);
        }

        resourceIds = builder.ToImmutable();
        return true;
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
    public Guid CorrelationId => _snapshot.Plan.CorrelationId;
    public ChannelOperationState State => _snapshot.State;
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
