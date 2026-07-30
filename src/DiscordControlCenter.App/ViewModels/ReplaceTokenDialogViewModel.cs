using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ReplaceTokenDialogViewModel : ObservableObject, IDisposable
{
    private readonly IBotProfileService _profileService;
    private readonly BotProfile _profile;
    private string _token = string.Empty;
    private string? _errorMessage;
    private string? _tokenError;
    private bool _isSaving;

    public ReplaceTokenDialogViewModel(
        IBotProfileService profileService,
        BotProfile profile)
    {
        _profileService = profileService;
        _profile = profile;
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave, HandleUnexpectedError);
    }

    public event EventHandler<bool>? RequestClose;

    public string DisplayName => _profile.DisplayName;

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
    public string SaveButtonText => IsSaving ? "Validating…" : "Validate and replace";

    public BotProfile? UpdatedProfile { get; private set; }
    public AsyncRelayCommand SaveCommand { get; }

    public void Cancel()
    {
        if (CanCancel)
        {
            RequestClose?.Invoke(this, false);
        }
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

    private bool CanSave() => !IsSaving && !string.IsNullOrWhiteSpace(Token);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var accepted = false;
        IsSaving = true;
        ErrorMessage = null;
        TokenError = null;
        try
        {
            var result = await _profileService.ReplaceTokenAsync(
                _profile.Id,
                Token,
                cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                TokenError = result.Error ?? "The token could not be replaced.";
                return;
            }

            UpdatedProfile = result.Value;
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

    private void HandleUnexpectedError(Exception exception)
    {
        _ = exception;
        ErrorMessage = "An unexpected error occurred while replacing the token.";
        IsSaving = false;
    }
}
