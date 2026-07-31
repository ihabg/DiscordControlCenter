using DiscordControlCenter.Application.Messaging;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ScheduledApprovalPreflightCheckItem(ScheduledApprovalPreflightCheck check)
{
    public string Id => check.Id.ToString();
    public string Label => check.Label;
    public string StateText => check.State switch
    {
        ScheduledApprovalPreflightState.Allowed => "Allowed",
        ScheduledApprovalPreflightState.Blocked => "Blocked",
        ScheduledApprovalPreflightState.Unavailable => "Unavailable",
        ScheduledApprovalPreflightState.Unknown => "Unknown",
        _ => "Not required"
    };
    public string StatusIcon => check.State switch
    {
        ScheduledApprovalPreflightState.Allowed => "✓",
        ScheduledApprovalPreflightState.Blocked => "!",
        ScheduledApprovalPreflightState.Unavailable => "…",
        ScheduledApprovalPreflightState.Unknown => "?",
        _ => "–"
    };
    public string Explanation => check.Explanation;
    public string? Remediation => check.Remediation;
    public bool IsBlocking => check.BlocksApproval;
}

public sealed class ContentUsageItem(ContentUsageRow row)
{
    public string Id => row.Id;
    public string Label => row.Label;
    public string UsageText => row.State == ContentUsageState.NotApplicable ? "Not applicable" : $"{row.Used:N0} / {row.Maximum:N0}";
    public string RemainingText => row.State == ContentUsageState.NotApplicable ? row.Summary : row.Summary;
    public string StateText => row.State switch
    {
        ContentUsageState.NotApplicable => "Not applicable",
        ContentUsageState.WithinLimit => "Within limit",
        ContentUsageState.NearLimit => "Near limit",
        _ => "Over limit"
    };
    public string StatusIcon => row.State switch
    {
        ContentUsageState.NotApplicable => "–",
        ContentUsageState.WithinLimit => "✓",
        ContentUsageState.NearLimit => "!",
        _ => "×"
    };
    public string? Warning => row.Warning;
    public bool BlocksApproval => row.BlocksApproval;
}

public sealed class MentionPolicyUsageItem(MentionPolicyUsageRow row)
{
    public string Id => row.Id;
    public string Label => row.Label;
    public string StateText => row.IsAllowed ? "Allowed" : "Blocked";
    public string StatusIcon => row.IsAllowed ? "✓" : "–";
    public string Summary => row.Summary;
}
