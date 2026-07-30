using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.ViewModels;

public sealed class BackupBrowserViewModel : ObservableObject, IDisposable
{
    private readonly IBackupCatalogService _catalog;
    private readonly IRecreateStructurePlanner _recreatePlanner;
    private readonly IOperationPlanSubmissionService _submission;
    private readonly IOperationExportService _export;
    private readonly IBotExplorerService _explorer;
    private BackupCatalogItem? _selectedBackup;
    private BackupDetail? _detail;
    private string _searchText = string.Empty;
    private int _pageNumber = 1;
    private int _totalPages;
    private bool _isLoading;
    private string? _errorMessage;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private string _botDisplayName = "Selected bot";
    private string _botFilter = string.Empty;
    private string _serverFilter = string.Empty;
    private string _createdFromFilter = string.Empty;
    private string _createdToFilter = string.Empty;
    private string _sourceOperationFilter = "All";
    private string _compatibilityFilter = "All";
    private BackupSort _selectedSort = BackupSort.Newest;
    private string _selectedNamingMode = "Add suffix";
    private bool _includePermissionOverwrites;
    private RecreateCompensationPolicy _selectedCompensationPolicy =
        RecreateCompensationPolicy.KeepSuccessfulResources;
    private bool _keepIndefinitely = true;
    private string _maximumAgeDays = string.Empty;
    private string _newestPerServer = string.Empty;
    private bool _preserveFailedBackups = true;
    private string _maximumStorageMegabytes = string.Empty;
    private CancellationTokenSource? _detailCancellation;

    public BackupBrowserViewModel(
        IBackupCatalogService catalog,
        IRecreateStructurePlanner recreatePlanner,
        IOperationPlanSubmissionService submission,
        IOperationExportService export,
        IBotExplorerService explorer)
    {
        _catalog = catalog;
        _recreatePlanner = recreatePlanner;
        _submission = submission;
        _export = export;
        _explorer = explorer;
        RefreshCommand = new AsyncRelayCommand(SearchAsync, errorHandler: HandleCommandError);
        NextPageCommand = new AsyncRelayCommand(
            NextAsync,
            () => PageNumber < TotalPages,
            HandleCommandError);
        PreviousPageCommand = new AsyncRelayCommand(
            PreviousAsync,
            () => PageNumber > 1,
            HandleCommandError);
        PinCommand = new AsyncRelayCommand(
            TogglePinAsync,
            () => SelectedBackup is not null,
            HandleCommandError);
        DeleteCommand = new AsyncRelayCommand(
            DeleteAsync,
            () => SelectedBackup is not null,
            HandleCommandError);
        RecreateCommand = new AsyncRelayCommand(
            RecreateAsync,
            () => Detail?.Backup is not null
                  && _botProfileId is not null
                  && _serverId is not null
                  && RecreateResources.Any(item => item.Include),
            HandleCommandError);
        CleanupCommand = new AsyncRelayCommand(CleanupAsync, errorHandler: HandleCommandError);
        ExportCommand = new AsyncRelayCommand(ExportAsync, errorHandler: HandleCommandError);
        ApplyNamingCommand = new RelayCommand(_ => ApplyNaming());
    }

    public ObservableCollection<BackupCatalogItem> Backups { get; } = [];
    public ObservableCollection<RecreateResourceItemViewModel> RecreateResources { get; } = [];
    public ObservableCollection<RoleMappingItemViewModel> RoleMappings { get; } = [];
    public IReadOnlyList<BackupSort> SortOptions { get; } = Enum.GetValues<BackupSort>();
    public IReadOnlyList<string> CompatibilityOptions { get; } =
        ["All", .. Enum.GetNames<BackupCompatibility>()];
    public IReadOnlyList<string> SourceOperationOptions { get; } =
        ["All", .. Enum.GetNames<ChannelOperationType>()];
    public IReadOnlyList<string> NamingModes { get; } =
        ["Use original", "Add suffix", "Add prefix", "Sequential replacement", "Manual"];
    public IReadOnlyList<RecreateCompensationPolicy> CompensationPolicies { get; } =
        Enum.GetValues<RecreateCompensationPolicy>();
    public BackupCatalogItem? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                _ = LoadDetailAsync(value);
                NotifyCommands();
            }
        }
    }

    public BackupDetail? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(DetailSummary));
                NotifyCommands();
            }
        }
    }

    public bool HasDetail => Detail is not null;
    public string DetailSummary => Detail is null
        ? "Select a backup to inspect its structural contents."
        : $"{Detail.Catalog.CategoryCount} categories, {Detail.Catalog.ChannelCount} channels, "
          + $"{Detail.Catalog.PermissionOverwriteCount} overwrites • {Detail.Catalog.Compatibility}";
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

    public string SourceOperationFilter
    {
        get => _sourceOperationFilter;
        set => SetProperty(ref _sourceOperationFilter, value);
    }

    public string CompatibilityFilter
    {
        get => _compatibilityFilter;
        set => SetProperty(ref _compatibilityFilter, value);
    }

    public BackupSort SelectedSort
    {
        get => _selectedSort;
        set => SetProperty(ref _selectedSort, value);
    }

    public string SelectedNamingMode
    {
        get => _selectedNamingMode;
        set => SetProperty(ref _selectedNamingMode, value);
    }

    public bool IncludePermissionOverwrites
    {
        get => _includePermissionOverwrites;
        set => SetProperty(ref _includePermissionOverwrites, value);
    }

    public RecreateCompensationPolicy SelectedCompensationPolicy
    {
        get => _selectedCompensationPolicy;
        set => SetProperty(ref _selectedCompensationPolicy, value);
    }

    public bool KeepIndefinitely
    {
        get => _keepIndefinitely;
        set => SetProperty(ref _keepIndefinitely, value);
    }

    public string MaximumAgeDays
    {
        get => _maximumAgeDays;
        set => SetProperty(ref _maximumAgeDays, value);
    }

    public string NewestPerServer
    {
        get => _newestPerServer;
        set => SetProperty(ref _newestPerServer, value);
    }

    public bool PreserveFailedBackups
    {
        get => _preserveFailedBackups;
        set => SetProperty(ref _preserveFailedBackups, value);
    }

    public string MaximumStorageMegabytes
    {
        get => _maximumStorageMegabytes;
        set => SetProperty(ref _maximumStorageMegabytes, value);
    }

    public int PageNumber
    {
        get => _pageNumber;
        private set => SetProperty(ref _pageNumber, value);
    }

    public int TotalPages
    {
        get => _totalPages;
        private set => SetProperty(ref _totalPages, value);
    }

    public string PageText => $"Page {PageNumber} of {Math.Max(TotalPages, 1)}";
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasBackups => Backups.Count > 0;
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand PinCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RecreateCommand { get; }
    public AsyncRelayCommand CleanupCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand ApplyNamingCommand { get; }

    public void SetContext(Guid? botProfileId, ulong? serverId, string? botDisplayName)
    {
        _botProfileId = botProfileId;
        _serverId = serverId;
        _botDisplayName = botDisplayName ?? "Selected bot";
        BuildRecreateChoices();
        NotifyCommands();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadRetentionAsync(cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public void Dispose()
    {
        RefreshCommand.Dispose();
        NextPageCommand.Dispose();
        PreviousPageCommand.Dispose();
        PinCommand.Dispose();
        DeleteCommand.Dispose();
        RecreateCommand.Dispose();
        CleanupCommand.Dispose();
        ExportCommand.Dispose();
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (!TryBuildQuery(out var query, out var validationError))
            {
                ErrorMessage = validationError;
                return;
            }

            var page = await _catalog.QueryAsync(
                query!,
                cancellationToken);
            Backups.Clear();
            foreach (var item in page.Items)
            {
                Backups.Add(item);
            }

            TotalPages = page.TotalPages;
            SelectedBackup = Backups.FirstOrDefault();
            OnPropertyChanged(nameof(HasBackups));
            OnPropertyChanged(nameof(PageText));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = "Backups could not be loaded. The database may contain an unsupported record.";
        }
        finally
        {
            IsLoading = false;
            NotifyCommands();
        }
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        PageNumber = 1;
        await LoadAsync(cancellationToken);
    }

    private async Task LoadDetailAsync(BackupCatalogItem? item)
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        var token = _detailCancellation.Token;
        try
        {
            Detail = item is null
                ? null
                : await _catalog.GetDetailAsync(item.BackupIdentifier, token);
            BuildRecreateChoices();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Detail = null;
            ErrorMessage = "The selected backup details could not be loaded safely.";
        }
    }

    private async Task NextAsync(CancellationToken cancellationToken)
    {
        PageNumber++;
        await LoadAsync(cancellationToken);
    }

    private async Task PreviousAsync(CancellationToken cancellationToken)
    {
        PageNumber--;
        await LoadAsync(cancellationToken);
    }

    private async Task TogglePinAsync(CancellationToken cancellationToken)
    {
        if (SelectedBackup is not { } selected)
        {
            return;
        }

        await _catalog.SetPinnedAsync(
            selected.BackupIdentifier,
            !selected.IsPinned,
            cancellationToken);
        await LoadAsync(cancellationToken);
    }

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (SelectedBackup is not { } selected
            || MessageBox.Show(
                $"Delete local backup {selected.BackupIdentifier}?\n\nThis deletes only the local structural record. It performs no Discord action.",
                "Delete local backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await _catalog.DeleteLocalAsync(
            [selected.BackupIdentifier],
            "User-confirmed local backup deletion",
            cancellationToken);
        await LoadAsync(cancellationToken);
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildRetentionPolicy(out var policy, out var error))
        {
            ErrorMessage = error;
            return;
        }

        var preview = await _catalog.PreviewCleanupAsync(policy, DateTimeOffset.UtcNow, cancellationToken);
        if (preview.Candidates.Length == 0)
        {
            await _catalog.SaveRetentionPolicyAsync(policy, cancellationToken);
            MessageBox.Show(
                "The retention policy was saved and selects no unpinned backups.",
                "Cleanup preview");
            return;
        }

        var exactList = string.Join(
            Environment.NewLine,
            preview.Candidates.Select(item =>
                $"• {item.BackupIdentifier} — {item.ServerName} — {item.Reason}"));
        if (MessageBox.Show(
                $"Delete {preview.Candidates.Length} unpinned local backups and reclaim about "
                + $"{preview.EstimatedBytesReclaimed:N0} bytes?\n\n{exactList}\n\n"
                + "No Discord resource is affected.",
                "Confirm local backup cleanup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await _catalog.SaveRetentionPolicyAsync(policy, cancellationToken);
        await _catalog.DeleteLocalAsync(
            preview.Candidates.Select(item => item.BackupIdentifier).ToArray(),
            "Confirmed retention cleanup",
            cancellationToken);
        await LoadAsync(cancellationToken);
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildQuery(out var query, out var validationError))
        {
            ErrorMessage = validationError;
            return;
        }

        query = query! with { PageNumber = 1, PageSize = 100 };
        var countPage = await _catalog.QueryAsync(
            query with { PageSize = 1 },
            cancellationToken);
        var dialog = new SaveFileDialog
        {
            Title = "Export safe backup metadata JSON",
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "backup-metadata.json"
        };
        if (dialog.ShowDialog() != true
            || MessageBox.Show(
                $"Export type: versioned backup metadata JSON\n"
                + $"Record count: {countPage.TotalCount:N0}\n"
                + $"Destination: {dialog.FileName}\n\n"
                + "Included: backup/operation/correlation IDs, bot/server IDs, timestamps, counts, schema, pin and size metadata.\n"
                + "Excluded: tokens, authorization, raw Discord payloads, messages, private content, stack traces, and user paths.",
                "Confirm safe backup export",
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
        await _export.ExportBackupMetadataJsonAsync(stream, query, cancellationToken);
    }

    private async Task RecreateAsync(CancellationToken cancellationToken)
    {
        if (Detail?.Backup is not { } backup
            || _botProfileId is not { } botId
            || _serverId is not { } serverId)
        {
            return;
        }

        var server = _explorer.GetSnapshot(botId).Servers.FirstOrDefault(item => item.Id == serverId);
        if (server is null)
        {
            ErrorMessage = "Connect the selected bot to an available target server first.";
            return;
        }

        var resources = RecreateResources
            .Select(item => item.ToSelection())
            .ToImmutableArray();
        var mappings = RoleMappings
            .Select(item => item.ToMapping())
            .ToImmutableArray();
        var request = new RecreateStructureRequest(
            botId,
            serverId,
            backup.BackupIdentifier,
            backup,
            resources,
            mappings,
            IncludePermissionOverwrites,
            SelectedCompensationPolicy,
            "Recreate replacement structure from local backup");
        var result = _recreatePlanner.Plan(request);
        if (!result.IsSuccess)
        {
            ErrorMessage = string.Join(" ", result.Errors);
            return;
        }

        var preview = _recreatePlanner.BuildPreview(result.Plan!, _botDisplayName);
        await _submission.ConfirmAndQueueAsync(result.Plan!, preview, cancellationToken);
    }

    private void NotifyCommands()
    {
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        PinCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RecreateCommand.NotifyCanExecuteChanged();
    }

    private void HandleCommandError(Exception exception)
    {
        _ = exception;
        ErrorMessage =
            "The requested local backup action could not be completed. No Discord action was performed.";
    }

    private bool TryBuildQuery(out BackupQuery? query, out string? error)
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

        var operation = Enum.TryParse<ChannelOperationType>(SourceOperationFilter, out var parsedOperation)
            ? parsedOperation
            : (ChannelOperationType?)null;
        var compatibility =
            Enum.TryParse<BackupCompatibility>(CompatibilityFilter, out var parsedCompatibility)
                ? parsedCompatibility
                : (BackupCompatibility?)null;
        query = new BackupQuery(
            SearchText,
            botId,
            serverId,
            from,
            to,
            operation,
            compatibility,
            SelectedSort,
            PageNumber,
            50);
        return true;
    }

    private async Task LoadRetentionAsync(CancellationToken cancellationToken)
    {
        var policy = await _catalog.GetRetentionPolicyAsync(cancellationToken);
        KeepIndefinitely = policy.KeepIndefinitely;
        MaximumAgeDays = policy.MaximumAgeDays?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        NewestPerServer = policy.NewestPerServer?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        PreserveFailedBackups = policy.PreserveFailedOperationBackups;
        MaximumStorageMegabytes = policy.MaximumStorageBytes is { } bytes
            ? (bytes / 1_048_576d).ToString("0.##", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private bool TryBuildRetentionPolicy(
        out BackupRetentionPolicy policy,
        out string? error)
    {
        policy = default!;
        error = null;
        if (!TryOptionalPositiveInt(MaximumAgeDays, out var maximumAge)
            || !TryOptionalPositiveInt(NewestPerServer, out var newest)
            || !TryOptionalPositiveDouble(MaximumStorageMegabytes, out var maximumMegabytes))
        {
            error = "Retention values must be positive numbers or blank.";
            return false;
        }

        policy = new BackupRetentionPolicy(
            KeepIndefinitely,
            maximumAge,
            newest,
            PreserveFailedBackups,
            maximumMegabytes is null
                ? null
                : checked((long)(maximumMegabytes.Value * 1_048_576)));
        return true;
    }

    private void BuildRecreateChoices()
    {
        RecreateResources.Clear();
        RoleMappings.Clear();
        if (Detail?.Backup is not { } backup
            || _botProfileId is not { } botId
            || _serverId is not { } serverId)
        {
            NotifyCommands();
            return;
        }

        var server = _explorer.GetSnapshot(botId).Servers.FirstOrDefault(item => item.Id == serverId);
        if (server is null)
        {
            NotifyCommands();
            return;
        }

        var categoryOptions = new[]
        {
            new ExistingCategoryOption(null, "Create replacement category")
        }.Concat(
            server.Channels
                .Where(channel => channel.Kind == ChannelKind.Category)
                .OrderBy(channel => channel.Position)
                .Select(channel =>
                    new ExistingCategoryOption(channel.Id, $"Reuse {channel.Name} ({channel.Id})")))
            .ToArray();
        for (var index = 0; index < backup.Channels.Length; index++)
        {
            var channel = backup.Channels[index];
            var item = new RecreateResourceItemViewModel(
                index,
                channel,
                categoryOptions,
                NotifyCommands);
            RecreateResources.Add(item);
        }

        foreach (var overwrite in backup.Channels
                     .SelectMany(channel => channel.PermissionOverwrites)
                     .DistinctBy(item => (item.TargetId, item.TargetType)))
        {
            RoleMappings.Add(new RoleMappingItemViewModel(overwrite, server));
        }

        ApplyNaming();
        NotifyCommands();
    }

    private void ApplyNaming()
    {
        var sequence = 1;
        foreach (var resource in RecreateResources)
        {
            resource.ProposedName = SelectedNamingMode switch
            {
                "Use original" => resource.OriginalName,
                "Add prefix" => $"replacement-{resource.OriginalName}",
                "Sequential replacement" => $"{resource.OriginalName}-replacement-{sequence++}",
                "Manual" => resource.ProposedName,
                _ => $"{resource.OriginalName}-replacement"
            };
        }
    }

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

    private static bool TryOptionalPositiveInt(string value, out int? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var result)
            || result < 1)
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static bool TryOptionalPositiveDouble(string value, out double? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var result)
            || result <= 0)
        {
            return false;
        }

        parsed = result;
        return true;
    }
}

public sealed record ExistingCategoryOption(ulong? Id, string Label);

public sealed class RecreateResourceItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _include;
    private string _proposedName;
    private ExistingCategoryOption _selectedCategoryOption;
    private bool _recreateUncategorized;

    public RecreateResourceItemViewModel(
        int backupIndex,
        ChannelOperationStateSnapshot source,
        IReadOnlyList<ExistingCategoryOption> categoryOptions,
        Action changed)
    {
        BackupIndex = backupIndex;
        Source = source;
        CategoryOptions = categoryOptions;
        _changed = changed;
        _include = source.Kind is ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice;
        _proposedName = source.Name;
        _selectedCategoryOption = categoryOptions[0];
    }

    public int BackupIndex { get; }
    public ChannelOperationStateSnapshot Source { get; }
    public string OriginalName => Source.Name;
    public string KindText => Source.Kind.ToString();
    public string ParentText => Source.ParentCategoryName ?? "Uncategorized";
    public string SupportText => Source.Kind is ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice
        ? "Supported"
        : "Unsupported and excluded";
    public bool IsCategory => Source.Kind == ChannelKind.Category;
    public IReadOnlyList<ExistingCategoryOption> CategoryOptions { get; }
    public bool Include
    {
        get => _include;
        set
        {
            if (SetProperty(ref _include, value))
            {
                _changed();
            }
        }
    }

    public string ProposedName
    {
        get => _proposedName;
        set => SetProperty(ref _proposedName, value);
    }

    public ExistingCategoryOption SelectedCategoryOption
    {
        get => _selectedCategoryOption;
        set => SetProperty(ref _selectedCategoryOption, value);
    }

    public bool RecreateUncategorized
    {
        get => _recreateUncategorized;
        set => SetProperty(ref _recreateUncategorized, value);
    }

    public RecreateResourceSelection ToSelection() =>
        new(
            BackupIndex,
            Include,
            ProposedName,
            IsCategory ? SelectedCategoryOption.Id : null,
            RecreateUncategorized);
}

public sealed record RoleTargetOption(
    ulong? TargetId,
    string? TargetName,
    RoleMappingChoice Choice,
    string Label);

public sealed class RoleMappingItemViewModel
{
    private readonly ChannelPermissionOverwriteSnapshot _source;

    public RoleMappingItemViewModel(
        ChannelPermissionOverwriteSnapshot source,
        ServerReadModel server)
    {
        _source = source;
        var options = new List<RoleTargetOption>
        {
            new(null, null, RoleMappingChoice.Skip, "Skip this overwrite")
        };
        if (source.TargetType == PermissionTargetKind.Role)
        {
            options.AddRange(
                server.Roles.Select(role =>
                {
                    var choice = role.IsEveryone
                        ? RoleMappingChoice.Everyone
                        : role.Id == source.TargetId
                            ? RoleMappingChoice.ExactId
                            : RoleMappingChoice.Manual;
                    var suggestion = role.Id != source.TargetId
                                     && string.Equals(
                                         role.Name,
                                         source.TargetDisplayName,
                                         StringComparison.OrdinalIgnoreCase)
                        ? " — suggested name match"
                        : string.Empty;
                    return new RoleTargetOption(
                        role.Id,
                        role.Name,
                        choice,
                        $"{role.Name} ({role.Id}){suggestion}");
                }));
        }

        Options = options;
        SelectedOption = options.FirstOrDefault(option =>
                             option.Choice is RoleMappingChoice.ExactId
                                 or RoleMappingChoice.Everyone)
                         ?? options[0];
    }

    public string OriginalText =>
        $"{_source.TargetDisplayName} ({_source.TargetType}, {_source.TargetId})";
    public IReadOnlyList<RoleTargetOption> Options { get; }
    public RoleTargetOption SelectedOption { get; set; }

    public RoleMapping ToMapping() =>
        new(
            _source.TargetId,
            _source.TargetType,
            _source.TargetDisplayName,
            SelectedOption.TargetId,
            SelectedOption.TargetName,
            SelectedOption.Choice,
            _source.TargetType == PermissionTargetKind.Role,
            true);
}
