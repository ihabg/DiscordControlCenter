using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.ViewModels;

public sealed class AddBotDialogViewModel : ObservableObject, IDisposable
{
    private readonly IBotProfileService _botProfileService;
    private string _displayName = string.Empty;
    private string _token = string.Empty;
    private string? _errorMessage;
    private string? _displayNameError;
    private string? _tokenError;
    private bool _isSaving;

    public AddBotDialogViewModel(IBotProfileService botProfileService)
    {
        _botProfileService = botProfileService;
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave, SetUnexpectedError);
    }

    public event EventHandler<bool>? RequestClose;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 80)
                {
                    DisplayNameError = null;
                }

                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Token
    {
        private get => _token;
        set
        {
            if (SetProperty(ref _token, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace))
                {
                    TokenError = null;
                }

                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? DisplayNameError
    {
        get => _displayNameError;
        private set
        {
            if (SetProperty(ref _displayNameError, value))
            {
                OnPropertyChanged(nameof(HasDisplayNameError));
            }
        }
    }

    public bool HasDisplayNameError => !string.IsNullOrWhiteSpace(DisplayNameError);

    public string? TokenError
    {
        get => _tokenError;
        private set
        {
            if (SetProperty(ref _tokenError, value))
            {
                OnPropertyChanged(nameof(HasTokenError));
            }
        }
    }

    public bool HasTokenError => !string.IsNullOrWhiteSpace(TokenError);

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

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(SaveButtonText));
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanCancel => !IsSaving;
    public string SaveButtonText => IsSaving ? "Validating…" : "Validate and save";

    public BotProfile? CreatedProfile { get; private set; }

    public AsyncRelayCommand SaveCommand { get; }

    public void Cancel()
    {
        if (CanCancel)
        {
            RequestClose?.Invoke(this, false);
        }
    }

    public void ValidateDisplayName()
    {
        DisplayNameError = string.IsNullOrWhiteSpace(DisplayName)
            ? "Display name is required."
            : DisplayName.Trim().Length > 80
                ? "Display name must be 80 characters or fewer."
                : null;
    }

    public void ValidateToken()
    {
        TokenError = string.IsNullOrWhiteSpace(_token)
            ? "Bot token is required."
            : _token.Any(char.IsWhiteSpace)
                ? "Bot token cannot contain whitespace."
                : null;
    }

    public void Dispose() => SaveCommand.Dispose();

    private bool CanSave() =>
        !IsSaving
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(Token);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var accepted = false;
        IsSaving = true;
        ErrorMessage = null;
        DisplayNameError = null;
        TokenError = null;
        try
        {
            var result = await _botProfileService
                .AddAsync(new AddBotRequest(DisplayName, Token), cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                AssignServiceError(result.Error ?? "The bot could not be added.");
                return;
            }

            CreatedProfile = result.Value;
            Token = string.Empty;
            accepted = true;
        }
        finally
        {
            IsSaving = false;
        }

        if (accepted)
        {
            RequestClose?.Invoke(this, true);
        }
    }

    private void AssignServiceError(string error)
    {
        if (error.StartsWith("Display name", StringComparison.OrdinalIgnoreCase))
        {
            DisplayNameError = error;
        }
        else if (error.Contains("token", StringComparison.OrdinalIgnoreCase)
                 || error.Contains("Discord", StringComparison.OrdinalIgnoreCase))
        {
            TokenError = error;
        }
        else
        {
            ErrorMessage = error;
        }
    }

    private void SetUnexpectedError(Exception exception)
    {
        _ = exception;
        ErrorMessage = "An unexpected error occurred while adding the bot.";
        IsSaving = false;
    }
}
