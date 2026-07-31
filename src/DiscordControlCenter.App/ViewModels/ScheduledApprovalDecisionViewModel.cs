using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ScheduledApprovalDecisionViewModel : ObservableObject
{
    private string _typedConfirmation = string.Empty;
    public ScheduledApprovalDecisionViewModel(string action, ScheduledMessageApproval approval)
    {
        Action = action; Approval = approval; RequiresTypedConfirmation = action == "Approve and send" && approval.ImmutableContent?.AllowedMentions.HasBroadMentions == true;
    }
    public string Action { get; }
    public ScheduledMessageApproval Approval { get; }
    public bool RequiresTypedConfirmation { get; }
    public string TypedConfirmation { get => _typedConfirmation; set { if (SetProperty(ref _typedConfirmation, value)) OnPropertyChanged(nameof(CanConfirm)); } }
    public bool CanConfirm => !RequiresTypedConfirmation || TypedConfirmation == "SEND MISSED MESSAGE";
}
