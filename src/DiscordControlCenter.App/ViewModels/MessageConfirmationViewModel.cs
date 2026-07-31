using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Application.Messaging;

namespace DiscordControlCenter.App.ViewModels;

public sealed class MessageConfirmationViewModel(MessagePreview preview) : ObservableObject
{
    private string _typedConfirmation = string.Empty;

    public MessagePreview Preview { get; } = preview;
    public string TypedConfirmation { get => _typedConfirmation; set { if (SetProperty(ref _typedConfirmation, value)) OnPropertyChanged(nameof(CanConfirm)); } }
    public bool RequiresTypedConfirmation => Preview.Plan.RequiresStrongConfirmation;
    public string ConfirmationInstruction => Preview.Plan.RequiredConfirmationText is { } required
        ? $"Type {required} to confirm this higher-impact delivery."
        : "Review the exact destination and preview before sending one message.";
    public bool CanConfirm => !RequiresTypedConfirmation || string.Equals(TypedConfirmation.Trim(), Preview.Plan.RequiredConfirmationText, StringComparison.Ordinal);
}
