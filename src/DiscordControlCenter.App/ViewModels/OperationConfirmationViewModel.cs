using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.ViewModels;

public sealed class OperationConfirmationViewModel : ObservableObject
{
    private string _confirmationText = string.Empty;
    private bool _submitted;

    public OperationConfirmationViewModel(
        OperationPlan plan,
        OperationPreview preview)
    {
        Plan = plan;
        Preview = preview;
        ConfirmCommand = new RelayCommand(
            _ => Submit(),
            _ => CanConfirm);
    }

    public event EventHandler? Confirmed;

    public OperationPlan Plan { get; }
    public OperationPreview Preview { get; }
    public bool RequiresTypedConfirmation =>
        Preview.ConfirmationRequirement.Kind != OperationConfirmationKind.Explicit;
    public bool HasPermissionOverwriteChanges =>
        Preview.PermissionOverwriteChanges.Length > 0;
    public string RiskText => $"{Preview.RiskLevel} risk";
    public string BackupText => Plan.IsDestructive
        ? "Required local structure backup will be saved before the first Discord request."
        : "No pre-operation backup is required for this non-destructive plan.";
    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (SetProperty(ref _confirmationText, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
                ConfirmCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanConfirm =>
        !_submitted
        && (!RequiresTypedConfirmation
            || string.Equals(
                ConfirmationText,
                Preview.ConfirmationRequirement.RequiredText,
                StringComparison.Ordinal));
    public RelayCommand ConfirmCommand { get; }

    private void Submit()
    {
        if (!CanConfirm)
        {
            return;
        }

        _submitted = true;
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
        Confirmed?.Invoke(this, EventArgs.Empty);
    }
}
