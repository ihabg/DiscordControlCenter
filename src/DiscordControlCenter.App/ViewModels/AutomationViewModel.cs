using System.Collections.ObjectModel;
using System.Collections.Immutable;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.ViewModels;

public sealed class AutomationViewModel : ObservableObject
{
    private readonly IAutomationRuleRepository _rules;
    private readonly IAutomationRulePreflightService _preflight;
    private readonly UiDispatcher _dispatcher;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private BotConnectionState _connectionState;
    private AutomationRule? _selectedRule;
    private string? _statusMessage;

    public AutomationViewModel(
        IAutomationRuleRepository rules,
        IAutomationRulePreflightService preflight,
        UiDispatcher dispatcher)
    {
        _rules = rules;
        _preflight = preflight;
        _dispatcher = dispatcher;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, errorHandler: _ => StatusMessage = "Automation rules could not be loaded.");
        CreateDraftCommand = new RelayCommand(_ => CreateDraft());
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync, canExecute: () => SelectedRule is not null,
            errorHandler: _ => StatusMessage = "The automation draft could not be saved.");
        ValidateCommand = new RelayCommand(_ => ValidateSelected());
    }

    public ObservableCollection<AutomationRule> Rules { get; } = [];
    public ObservableCollection<string> PreflightIssues { get; } = [];
    public AutomationRule? SelectedRule { get => _selectedRule; set { if (SetProperty(ref _selectedRule, value)) ValidateSelected(); } }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasContext => _botProfileId is not null && _serverId is not null && _connectionState == BotConnectionState.Connected;
    public string ContextMessage => !HasContext
        ? "Select a connected bot and server. Member-join automation also requires Server Members Intent in the Developer Portal and local bot profile."
        : "Rules remain drafts until their exact actions pass preflight and are explicitly enabled.";
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CreateDraftCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public RelayCommand ValidateCommand { get; }

    public void SetContext(Guid? botProfileId, BotConnectionState state, ulong? serverId)
    {
        _botProfileId = botProfileId;
        _connectionState = state;
        _serverId = serverId;
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
        _ = RefreshAsync();
    }

    public void SetConnectionState(BotConnectionState state)
    {
        _connectionState = state;
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
        ValidateSelected();
    }

    public void SetServer(ulong? serverId)
    {
        _serverId = serverId;
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try { await LoadAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { StatusMessage = "Automation rules could not be loaded."; }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await _rules.ListAsync(_botProfileId, _serverId, cancellationToken).ConfigureAwait(false);
        _dispatcher.Post(() => { Rules.Clear(); foreach (var rule in loaded) Rules.Add(rule); SelectedRule = Rules.FirstOrDefault(); });
    }

    private void CreateDraft()
    {
        if (_botProfileId is not Guid botId || _serverId is not ulong serverId)
        {
            StatusMessage = "Select a bot and server before creating an automation draft.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SelectedRule = new AutomationRule(
            Guid.NewGuid(), 1, "New join workflow", botId, serverId, "Selected server",
            AutomationTrigger.MemberJoinedServer, ImmutableArray<AutomationCondition>.Empty,
            ImmutableArray<AutomationAction>.Empty, AutomationRuleState.Draft,
            AutomationRateLimitPolicy.ConservativeDefault, now, now,
            "Draft only; add bounded actions and complete preflight before enabling.");
        StatusMessage = "Created a local draft. It has no actions and cannot run or be enabled.";
        SaveDraftCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveDraftAsync(CancellationToken cancellationToken)
    {
        if (SelectedRule is not { } selected)
        {
            return;
        }

        var exists = Rules.Any(rule => rule.Id == selected.Id);
        var saved = selected with
        {
            Version = exists ? selected.Version + 1 : selected.Version,
            State = AutomationRuleState.Draft,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _rules.SaveVersionAsync(saved, cancellationToken).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            var existingIndex = Rules.IndexOf(Rules.FirstOrDefault(rule => rule.Id == saved.Id)!);
            if (existingIndex >= 0)
            {
                Rules[existingIndex] = saved;
            }
            else
            {
                Rules.Insert(0, saved);
            }

            SelectedRule = saved;
            StatusMessage = $"Saved draft version {saved.Version}. Drafts cannot run until actions pass preflight and are explicitly enabled.";
        });
    }

    private void ValidateSelected()
    {
        PreflightIssues.Clear();
        SaveDraftCommand.NotifyCanExecuteChanged();
        if (SelectedRule is null) return;
        foreach (var issue in _preflight.Validate(SelectedRule).Issues) PreflightIssues.Add(issue.Message);
    }
}
