using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class MemberItemViewModel(MemberReadModel model) : ObservableObject
{
    private MemberReadModel _model = model;

    public MemberReadModel Model => _model;
    public ulong Id => _model.Id;
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    public string DisplayName => _model.DisplayName;
    public string Username => _model.Username;
    public string GlobalDisplayName => _model.GlobalDisplayName ?? "Unavailable";
    public string Nickname => _model.Nickname ?? "None";
    public string? AvatarUrl => _model.AvatarUrl;
    public string AccountType => _model.IsBot ? "Bot" : "Human";
    public string CreatedAtText => _model.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    public string JoinedAtText => FormatDate(_model.JoinedAt);
    public string HighestRole => _model.HighestRoleName ?? "Unavailable";
    public string RoleCountText => _model.RolesAreComplete
        ? _model.RoleIds.Length.ToString(CultureInfo.CurrentCulture)
        : "Partial";
    public string BoostingText => _model.BoostStartedAt is null ? "No" : "Yes";
    public string BoostStartedText => FormatDate(_model.BoostStartedAt);
    public string PendingText => FormatBoolean(_model.IsPending);
    public string TimeoutText => FormatDate(_model.TimedOutUntil);
    public string VoiceChannel => _model.VoiceState?.ChannelName ?? "Not in accessible voice";
    public bool IsBot => _model.IsBot;
    public bool IsInVoice => _model.VoiceState is not null;
    public bool IsBoosting => _model.BoostStartedAt is not null;
    public bool IsTimedOut => _model.TimedOutUntil > DateTimeOffset.UtcNow;
    public bool IsPending => _model.IsPending == true;
    public string VoiceFlags => _model.VoiceState is null
        ? "Unavailable"
        : $"Self mute: {YesNo(_model.VoiceState.IsSelfMuted)} · Self deaf: {YesNo(_model.VoiceState.IsSelfDeafened)} · "
            + $"Server mute: {YesNo(_model.VoiceState.IsServerMuted)} · Server deaf: {YesNo(_model.VoiceState.IsServerDeafened)} · "
            + $"Streaming: {YesNo(_model.VoiceState.IsStreaming)} · Video: {YesNo(_model.VoiceState.IsVideoing)}";

    public void Update(MemberReadModel model)
    {
        if (model == _model)
        {
            return;
        }

        _model = model;
        OnPropertyChanged(string.Empty);
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) ?? "Unavailable";

    private static string FormatBoolean(bool? value) =>
        value switch
        {
            true => "Yes",
            false => "No",
            null => "Unavailable"
        };

    private static string YesNo(bool value) => value ? "Yes" : "No";
}

public sealed record RoleFilterOption(ulong? Id, string Name)
{
    public override string ToString() => Name;
}
