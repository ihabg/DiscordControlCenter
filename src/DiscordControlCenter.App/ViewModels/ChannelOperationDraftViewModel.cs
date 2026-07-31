using System.Collections.Immutable;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ChannelOperationDraftViewModel : ObservableObject
{
    private readonly ChannelOperationContext _context;
    private readonly IChannelOperationPlanner _planner;
    private string _namesText = string.Empty;
    private ChannelKind _selectedChannelType = ChannelKind.Text;
    private ChannelOptionViewModel? _selectedCategory;
    private ChannelOptionViewModel? _selectedAnchor;
    private RoleOptionViewModel? _selectedRole;
    private string _newName = string.Empty;
    private string _topic = string.Empty;
    private string _renameValue = string.Empty;
    private string _replacementValue = string.Empty;
    private BulkRenameMode _renameMode = BulkRenameMode.Prefix;
    private MovePlacement _movePlacement = MovePlacement.PreserveRelativeOrder;
    private int _slowModeSeconds;
    private int _bitrate = 64000;
    private int _userLimit;
    private int _startNumber = 1;
    private int _zeroPadding = 2;
    private int _position = -1;
    private int _defaultAutoArchiveMinutes = 60;
    private bool _isNsfw;
    private bool _copyOverwrites = true;
    private bool _copyCategoryOverwrites = true;
    private bool _copyChildOverwrites = true;
    private bool _synchronizeClonedChildren;
    private bool _includeSecondaryPermission;
    private bool _isUnlock;
    private bool _deleteCategoryOnly = true;
    private bool _includeAllChildren = true;
    private string _auditReason = string.Empty;
    private string _regionOverride = string.Empty;
    private string? _validationMessage;

    public ChannelOperationDraftViewModel(
        ChannelOperationContext context,
        ChannelOperationUiMode mode,
        IChannelOperationPlanner planner)
    {
        _context = context;
        Mode = mode;
        _planner = planner;
        Categories =
        [
            new ChannelOptionViewModel(null, "Uncategorized"),
            .. context.Server.Channels
                .Where(channel => channel.Kind == ChannelKind.Category)
                .OrderBy(channel => channel.Position)
                .Select(channel => new ChannelOptionViewModel(channel.Id, channel.Name))
        ];
        Anchors = context.Server.Channels
            .Where(channel => channel.Kind != ChannelKind.Category)
            .OrderBy(channel => channel.CategoryId)
            .ThenBy(channel => channel.Position)
            .Select(channel => new ChannelOptionViewModel(channel.Id, channel.Name))
            .ToArray();
        Roles = context.Server.Roles
            .OrderByDescending(role => role.Position)
            .Select(role => new RoleOptionViewModel(role.Id, role.Name, role.IsEveryone))
            .ToArray();
        SelectedCategory = Categories[0];
        SelectedRole = FindRole(role => role.IsEveryone)
            ?? (Roles.Count == 0 ? null : Roles[0]);
        ConfigureDefaults();
    }

    public ChannelOperationUiMode Mode { get; }
    public string Title => Mode switch
    {
        ChannelOperationUiMode.Create => "Create channels",
        ChannelOperationUiMode.Edit => "Edit channel",
        ChannelOperationUiMode.Rename => "Rename selected channels",
        ChannelOperationUiMode.Move => "Move or reorder channels",
        ChannelOperationUiMode.Clone => "Clone structure",
        ChannelOperationUiMode.Lock => "Lock or unlock channels",
        ChannelOperationUiMode.SynchronizePermissions => "Synchronize permissions",
        ChannelOperationUiMode.Delete => "Configure deletion",
        _ => "Channel operation"
    };

    public string ScopeSummary =>
        $"{_context.BotDisplayName} • {_context.Server.Name} • {_context.SelectedChannels.Length} selected";
    public IReadOnlyList<ChannelKind> CreatableTypes { get; } =
        [ChannelKind.Category, ChannelKind.Text, ChannelKind.Voice];
    public IReadOnlyList<BulkRenameMode> RenameModes { get; } = Enum.GetValues<BulkRenameMode>();
    public IReadOnlyList<MovePlacement> MovePlacements { get; } = Enum.GetValues<MovePlacement>();
    public IReadOnlyList<ChannelOptionViewModel> Categories { get; }
    public IReadOnlyList<ChannelOptionViewModel> Anchors { get; }
    public IReadOnlyList<RoleOptionViewModel> Roles { get; }
    public bool IsCreate => Mode == ChannelOperationUiMode.Create;
    public bool IsEdit => Mode == ChannelOperationUiMode.Edit;
    public bool IsRename => Mode == ChannelOperationUiMode.Rename;
    public bool IsMove => Mode == ChannelOperationUiMode.Move;
    public bool IsClone => Mode == ChannelOperationUiMode.Clone;
    public bool IsLock => Mode == ChannelOperationUiMode.Lock;
    public bool IsDelete => Mode == ChannelOperationUiMode.Delete;
    public bool IsSimplePreview =>
        Mode == ChannelOperationUiMode.SynchronizePermissions;
    public bool IsCategoryClone =>
        IsClone && _context.SelectedChannels.Any(channel => channel.Kind == ChannelKind.Category);
    public bool IsIndividualClone => IsClone && !IsCategoryClone;
    public bool IsCategoryDelete =>
        IsDelete && _context.SelectedChannels.Any(channel => channel.Kind == ChannelKind.Category);
    public IReadOnlyList<int> AutoArchiveDurations { get; } = [60, 1440, 4320, 10080];
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string NamesText
    {
        get => _namesText;
        set
        {
            if (SetProperty(ref _namesText, value))
            {
                OnPropertyChanged(nameof(ParsedNameCountText));
            }
        }
    }

    public string ParsedNameCountText
    {
        get
        {
            var count = NamesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            return count == 0 ? "Add one channel name per line." : $"{count} channel{(count == 1 ? string.Empty : "s")} will be included in the preview.";
        }
    }

    public ChannelKind SelectedChannelType
    {
        get => _selectedChannelType;
        set
        {
            if (SetProperty(ref _selectedChannelType, value))
            {
                OnPropertyChanged(nameof(ShowsTextFields));
                OnPropertyChanged(nameof(ShowsVoiceFields));
            }
        }
    }

    public ChannelOptionViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(CanCopyParentOverwrites));
            }
        }
    }

    public bool CanCopyParentOverwrites => SelectedCategory?.Id is not null;

    public bool IsCustomPosition
    {
        get => Position >= 0;
        set
        {
            Position = value ? Math.Max(0, Position) : -1;
            OnPropertyChanged(nameof(IsCustomPosition));
            OnPropertyChanged(nameof(CustomPosition));
        }
    }

    public int CustomPosition
    {
        get => Math.Max(0, Position);
        set
        {
            Position = Math.Max(0, value);
            OnPropertyChanged(nameof(IsCustomPosition));
            OnPropertyChanged(nameof(CustomPosition));
        }
    }

    public ChannelOptionViewModel? SelectedAnchor
    {
        get => _selectedAnchor;
        set => SetProperty(ref _selectedAnchor, value);
    }

    public RoleOptionViewModel? SelectedRole
    {
        get => _selectedRole;
        set => SetProperty(ref _selectedRole, value);
    }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string Topic
    {
        get => _topic;
        set => SetProperty(ref _topic, value);
    }

    public string RenameValue
    {
        get => _renameValue;
        set => SetProperty(ref _renameValue, value);
    }

    public string ReplacementValue
    {
        get => _replacementValue;
        set => SetProperty(ref _replacementValue, value);
    }

    public BulkRenameMode RenameMode
    {
        get => _renameMode;
        set => SetProperty(ref _renameMode, value);
    }

    public MovePlacement MovePlacement
    {
        get => _movePlacement;
        set => SetProperty(ref _movePlacement, value);
    }

    public int SlowModeSeconds
    {
        get => _slowModeSeconds;
        set => SetProperty(ref _slowModeSeconds, value);
    }

    public int Bitrate
    {
        get => _bitrate;
        set => SetProperty(ref _bitrate, value);
    }

    public int UserLimit
    {
        get => _userLimit;
        set => SetProperty(ref _userLimit, value);
    }

    public int StartNumber
    {
        get => _startNumber;
        set => SetProperty(ref _startNumber, value);
    }

    public int ZeroPadding
    {
        get => _zeroPadding;
        set => SetProperty(ref _zeroPadding, value);
    }

    public int Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(IsCustomPosition));
                OnPropertyChanged(nameof(CustomPosition));
            }
        }
    }

    public int DefaultAutoArchiveMinutes
    {
        get => _defaultAutoArchiveMinutes;
        set => SetProperty(ref _defaultAutoArchiveMinutes, value);
    }

    public bool IsNsfw
    {
        get => _isNsfw;
        set => SetProperty(ref _isNsfw, value);
    }

    public bool CopyOverwrites
    {
        get => _copyOverwrites;
        set => SetProperty(ref _copyOverwrites, value);
    }

    public bool CopyCategoryOverwrites
    {
        get => _copyCategoryOverwrites;
        set => SetProperty(ref _copyCategoryOverwrites, value);
    }

    public bool CopyChildOverwrites
    {
        get => _copyChildOverwrites;
        set => SetProperty(ref _copyChildOverwrites, value);
    }

    public bool SynchronizeClonedChildren
    {
        get => _synchronizeClonedChildren;
        set => SetProperty(ref _synchronizeClonedChildren, value);
    }

    public bool IncludeSecondaryPermission
    {
        get => _includeSecondaryPermission;
        set => SetProperty(ref _includeSecondaryPermission, value);
    }

    public bool IsUnlock
    {
        get => _isUnlock;
        set => SetProperty(ref _isUnlock, value);
    }

    public bool DeleteCategoryOnly
    {
        get => _deleteCategoryOnly;
        set => SetProperty(ref _deleteCategoryOnly, value);
    }

    public bool IncludeAllChildren
    {
        get => _includeAllChildren;
        set => SetProperty(ref _includeAllChildren, value);
    }

    public string AuditReason
    {
        get => _auditReason;
        set => SetProperty(ref _auditReason, value);
    }

    public string RegionOverride
    {
        get => _regionOverride;
        set => SetProperty(ref _regionOverride, value);
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool ShowsTextFields =>
        IsEdit
            ? _context.SelectedChannels.FirstOrDefault()?.Kind == ChannelKind.Text
            : IsCreate && SelectedChannelType == ChannelKind.Text;
    public bool ShowsVoiceFields =>
        IsEdit
            ? _context.SelectedChannels.FirstOrDefault()?.Kind == ChannelKind.Voice
            : IsCreate && SelectedChannelType == ChannelKind.Voice;

    public OperationPlan? TryBuildPlan()
    {
        var result = Mode switch
        {
            ChannelOperationUiMode.Create => BuildCreate(),
            ChannelOperationUiMode.Edit => BuildEdit(),
            ChannelOperationUiMode.Rename => BuildRename(),
            ChannelOperationUiMode.Move => BuildMove(),
            ChannelOperationUiMode.Clone => BuildClone(),
            ChannelOperationUiMode.Lock => BuildLock(),
            ChannelOperationUiMode.SynchronizePermissions => BuildSynchronize(),
            ChannelOperationUiMode.Delete => BuildDelete(),
            _ => ChannelPlanResult.Failure("Unsupported channel operation.")
        };
        ValidationMessage = result.IsSuccess ? null : string.Join(Environment.NewLine, result.Errors);
        return result.Plan;
    }

    private ChannelPlanResult BuildCreate()
    {
        var names = NamesText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToImmutableArray();
        var items = names
            .Select(
                name => new ChannelCreationItem(
                    name,
                    SelectedChannelType,
                    SelectedChannelType == ChannelKind.Category ? null : SelectedCategory?.Id,
                    ShowsTextFields ? Topic : null,
                    ShowsTextFields ? IsNsfw : null,
                    ShowsTextFields ? SlowModeSeconds : null,
                    ShowsVoiceFields ? Bitrate : null,
                    ShowsVoiceFields ? UserLimit : null,
                    Position >= 0 ? Position : null,
                    CopyOverwrites && SelectedCategory?.Id is not null))
            .ToImmutableArray();
        return _planner.PlanCreate(
            new CreateChannelsRequest(
                _context.BotProfileId,
                _context.Server.Id,
                items,
                AuditReason));
    }

    private ChannelPlanResult BuildEdit()
    {
        var channel = _context.SelectedChannels.Single();
        return _planner.PlanEdit(
            new EditChannelRequest(
                _context.BotProfileId,
                _context.Server.Id,
                channel.Id,
                OptionalChange.To(NewName),
                OptionalChange.To<ulong?>(channel.Kind == ChannelKind.Category ? null : SelectedCategory?.Id),
                OptionalChange.To(Position),
                channel.Kind == ChannelKind.Text
                    ? OptionalChange.To<string?>(Topic)
                    : OptionalChange.Unchanged<string?>(),
                channel.Kind == ChannelKind.Text
                    ? OptionalChange.To(IsNsfw)
                    : OptionalChange.Unchanged<bool>(),
                channel.Kind == ChannelKind.Text
                    ? OptionalChange.To(SlowModeSeconds)
                    : OptionalChange.Unchanged<int>(),
                channel.Kind == ChannelKind.Text
                    ? OptionalChange.To(DefaultAutoArchiveMinutes)
                    : OptionalChange.Unchanged<int>(),
                channel.Kind == ChannelKind.Voice
                    ? OptionalChange.To(Bitrate)
                    : OptionalChange.Unchanged<int>(),
                channel.Kind == ChannelKind.Voice
                    ? OptionalChange.To(UserLimit)
                    : OptionalChange.Unchanged<int>(),
                channel.Kind == ChannelKind.Voice
                    ? OptionalChange.To<string?>(
                        string.IsNullOrWhiteSpace(RegionOverride) ? null : RegionOverride)
                    : OptionalChange.Unchanged<string?>(),
                AuditReason));
    }

    private ChannelPlanResult BuildRename() =>
        _planner.PlanBulkRename(
            new BulkRenameRequest(
                _context.BotProfileId,
                _context.Server.Id,
                _context.SelectedChannels.Select(channel => channel.Id).ToImmutableArray(),
                RenameMode,
                RenameValue,
                ReplacementValue,
                StartNumber,
                ZeroPadding,
                AuditReason));

    private ChannelPlanResult BuildMove() =>
        _planner.PlanMove(
            new MoveChannelsRequest(
                _context.BotProfileId,
                _context.Server.Id,
                _context.SelectedChannels.Select(channel => channel.Id).ToImmutableArray(),
                SelectedCategory?.Id,
                MovePlacement,
                SelectedAnchor?.Id,
                AuditReason));

    private ChannelPlanResult BuildClone()
    {
        var source = _context.SelectedChannels.First();
        if (source.Kind == ChannelKind.Category)
        {
            var children = _context.SelectedChannels
                .Where(channel => channel.Kind != ChannelKind.Category && channel.CategoryId == source.Id)
                .Select(channel => channel.Id)
                .ToImmutableArray();
            if (children.Length == 0 && IncludeAllChildren)
            {
                children = _context.Server.Channels
                    .Where(channel => channel.CategoryId == source.Id)
                    .Select(channel => channel.Id)
                    .ToImmutableArray();
            }

            return _planner.PlanCloneCategory(
                new CloneCategoryRequest(
                    _context.BotProfileId,
                    _context.Server.Id,
                    source.Id,
                    NewName,
                    children,
                    CopyCategoryOverwrites,
                    CopyChildOverwrites,
                    SynchronizeClonedChildren,
                    AuditReason));
        }

        return _planner.PlanClone(
            new CloneChannelRequest(
                _context.BotProfileId,
                _context.Server.Id,
                source.Id,
                NewName,
                SelectedCategory?.Id,
                CopyOverwrites,
                AuditReason));
    }

    private ChannelPlanResult BuildLock() =>
        SelectedRole is null
            ? ChannelPlanResult.Failure("Choose a target role.")
            : _planner.PlanLock(
                new ChannelLockRequest(
                    _context.BotProfileId,
                    _context.Server.Id,
                    _context.SelectedChannels.Select(channel => channel.Id).ToImmutableArray(),
                    SelectedRole.Id,
                    IsUnlock,
                    IncludeSecondaryPermission,
                    AuditReason));

    private ChannelPlanResult BuildSynchronize() =>
        _planner.PlanSynchronizePermissions(
            new SynchronizePermissionsRequest(
                _context.BotProfileId,
                _context.Server.Id,
                _context.SelectedChannels.Select(channel => channel.Id).ToImmutableArray(),
                AuditReason));

    private ChannelPlanResult BuildDelete()
    {
        var category = _context.SelectedChannels.FirstOrDefault(channel => channel.Kind == ChannelKind.Category);
        var selectedChildren = category is null
            ? ImmutableArray<ulong>.Empty
            : _context.SelectedChannels
                .Where(channel => channel.CategoryId == category.Id)
                .Select(channel => channel.Id)
                .ToImmutableArray();
        return _planner.PlanDelete(
            new DeleteChannelsRequest(
                _context.BotProfileId,
                _context.Server.Id,
                category is null
                    ? _context.SelectedChannels.Select(channel => channel.Id).ToImmutableArray()
                    : [category.Id],
                category is not null && DeleteCategoryOnly,
                category is not null && !DeleteCategoryOnly && IncludeAllChildren,
                selectedChildren,
                AuditReason));
    }

    private void ConfigureDefaults()
    {
        var selected = _context.SelectedChannels.FirstOrDefault();
        switch (Mode)
        {
            case ChannelOperationUiMode.Create:
                NamesText = "new-channel";
                break;
            case ChannelOperationUiMode.Edit when selected is not null:
                NewName = selected.Name;
                Topic = selected.Topic ?? string.Empty;
                SlowModeSeconds = selected.SlowModeSeconds ?? 0;
                DefaultAutoArchiveMinutes = selected.DefaultAutoArchiveMinutes ?? 60;
                Bitrate = selected.Bitrate ?? 64000;
                UserLimit = selected.UserLimit ?? 0;
                RegionOverride = selected.RegionOverride ?? string.Empty;
                Position = selected.Position;
                IsNsfw = selected.IsNsfw == true;
                SelectedCategory = FindCategory(category => category.Id == selected.CategoryId)
                    ?? Categories[0];
                break;
            case ChannelOperationUiMode.Rename:
                RenameValue = "phase4-";
                break;
            case ChannelOperationUiMode.Move:
                SelectedCategory = FindCategory(category =>
                    category.Id != selected?.CategoryId) ?? Categories[0];
                SelectedAnchor = Anchors.Count == 0 ? null : Anchors[0];
                break;
            case ChannelOperationUiMode.Clone when selected is not null:
                NewName = $"{selected.Name}-copy";
                SelectedCategory = FindCategory(category => category.Id == selected.CategoryId)
                    ?? Categories[0];
                break;
        }
    }

    private ChannelOptionViewModel? FindCategory(
        Func<ChannelOptionViewModel, bool> predicate)
    {
        foreach (var category in Categories)
        {
            if (predicate(category))
            {
                return category;
            }
        }

        return null;
    }

    private RoleOptionViewModel? FindRole(
        Func<RoleOptionViewModel, bool> predicate)
    {
        foreach (var role in Roles)
        {
            if (predicate(role))
            {
                return role;
            }
        }

        return null;
    }
}

public sealed record ChannelOptionViewModel(ulong? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record RoleOptionViewModel(ulong Id, string Name, bool IsEveryone)
{
    public override string ToString() => IsEveryone ? $"{Name} (everyone)" : Name;
}
