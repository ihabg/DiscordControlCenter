using System.Collections.Immutable;
using System.Text.Json;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Messaging;
using DiscordControlCenter.Core.Persistence;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Core.Security;
using DiscordControlCenter.Discord;
using DiscordControlCenter.Infrastructure.Configuration;
using DiscordControlCenter.Infrastructure.Persistence;
using DiscordControlCenter.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace DiscordControlCenter.LiveValidation;

internal static class Program
{
    private const string ValidationChannelName = "dcc-live-validation";

    private static async Task<int> Main(string[] args)
    {
        if (!RunnerArguments.TryParse(args, out var options, out var argumentError))
        {
            Console.Error.WriteLine(argumentError);
            return 2;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var provider = BuildServices();
        var services = provider;
        var database = services.GetRequiredService<IDatabaseInitializer>();
        var profiles = services.GetRequiredService<IBotProfileRepository>();
        var connections = services.GetRequiredService<IBotConnectionManager>();
        var explorer = services.GetRequiredService<IBotExplorerService>();
        var repository = services.GetRequiredService<IScheduledMessageRepository>();
        var preflight = services.GetRequiredService<IScheduledApprovalPreflightService>();
        var approvals = services.GetRequiredService<IScheduledApprovalService>();
        var verifier = services.GetRequiredService<ILiveValidationMessageVerifier>();
        BotProfile? profile = null;
        ulong? createdChannelId = null;
        try
        {
            await database.InitializeAsync(cancellation.Token).ConfigureAwait(false);
            await connections.InitializeAsync(cancellation.Token).ConfigureAwait(false);
            var enabledProfiles = (await profiles.GetAllAsync(cancellation.Token).ConfigureAwait(false)).Where(item => item.IsEnabled).ToArray();
            if (enabledProfiles.Length != 1)
            {
                return Fail("Exactly one enabled configured bot profile is required for live validation.");
            }

            profile = enabledProfiles[0];
            var connect = await connections.ConnectAsync(profile.Id, cancellation.Token).ConfigureAwait(false);
            if (!connect.IsSuccess)
            {
                return Fail("The configured test bot could not connect.");
            }

            var refresh = await ((BotConnectionManager)connections).RefreshAsync(profile.Id, cancellation.Token).ConfigureAwait(false);
            if (!refresh.IsSuccess)
            {
                return Fail("The configured test bot could not refresh accessible server structure.");
            }

            var servers = explorer.GetSnapshot(profile.Id).Servers
                .Where(server => server.Availability == ServerAvailability.Available && server.Name == options!.ServerName).ToArray();
            if (servers.Length != 1)
            {
                return Fail(servers.Length == 0 ? "No exact teast server is accessible to the configured bot." : "Multiple exact teast server matches were found.");
            }

            var server = servers[0];
            var channels = server.Channels.Where(channel => channel.Kind == ChannelKind.Text && channel.Name == ValidationChannelName).ToArray();
            if (channels.Length > 1)
            {
                return Fail("Multiple disposable validation channels were found; runner stopped safely.");
            }

            if (channels.Length == 0)
            {
                var planner = services.GetRequiredService<IChannelOperationPlanner>();
                var executor = services.GetRequiredService<IChannelOperationExecutor>();
                var planResult = planner.PlanCreate(new CreateChannelsRequest(profile.Id, server.Id, [new ChannelCreationItem(ValidationChannelName, ChannelKind.Text, null, "Discord Control Center live validation only", false, 0, null, null, null, false)], "Live validation temporary channel"));
                if (!planResult.IsSuccess || planResult.Plan is null) return Fail("The guarded temporary-channel plan was rejected.");
                var created = await executor.ExecuteAsync(planResult.Plan, null, cancellation.Token).ConfigureAwait(false);
                if (created.State != ChannelOperationState.Completed) return Fail("The guarded temporary-channel operation did not complete.");
                await ((BotConnectionManager)connections).RefreshAsync(profile.Id, cancellation.Token).ConfigureAwait(false);
                server = explorer.GetSnapshot(profile.Id).Servers.Single(item => item.Id == server.Id);
                channels = server.Channels.Where(item => item.Kind == ChannelKind.Text && item.Name == ValidationChannelName).ToArray();
                if (channels.Length != 1) return Fail("The created temporary validation channel could not be uniquely confirmed.");
                createdChannelId = channels[0].Id;
            }

            var channel = channels[0];
            var validationId = Guid.NewGuid().ToString("N");
            var approve = await ReserveAsync(repository, profile.Id, server, channel, "DCC Live Validation — Approve", validationId, cancellation.Token).ConfigureAwait(false);
            var approval = await repository.GetApprovalAsync(approve.Id, cancellation.Token).ConfigureAwait(false) ?? throw new InvalidOperationException("Reserved approval was not found.");
            var preflightResult = await preflight.EvaluateAsync(approval, cancellation.Token).ConfigureAwait(false);
            if (!IsExpectedPreflight(preflightResult)) return Fail("The required fourteen approval checks did not permit the controlled plain-message occurrence.");

            var delivery = await approvals.ApproveAsync(approve.Id, cancellation.Token).ConfigureAwait(false);
            var delivered = await repository.GetApprovalAsync(approve.Id, cancellation.Token).ConfigureAwait(false);
            if (delivery?.State != MessageOperationState.Delivered || delivered?.Occurrence.State != MessageOperationState.Delivered)
            {
                return Fail("Approved occurrence did not reach Delivered; no further Discord write will be attempted.");
            }

            var matchingMessages = delivery.DiscordMessageId is ulong messageId && await verifier.MessageExistsAsync(profile.Id, channel.Id, messageId, cancellation.Token).ConfigureAwait(false) ? 1 : 0;
            if (matchingMessages != 1) return Fail("The returned Discord message ID could not be confirmed in the temporary channel.");

            var duplicateRejected = await approvals.ApproveAsync(approve.Id, cancellation.Token).ConfigureAwait(false) is null;
            var skip = await ReserveAsync(repository, profile.Id, server, channel, "DCC Live Validation — Skip", Guid.NewGuid().ToString("N"), cancellation.Token).ConfigureAwait(false);
            var skipResult = await approvals.SkipAsync(skip.Id, cancellation.Token).ConfigureAwait(false);
            var archive = await ReserveAsync(repository, profile.Id, server, channel, "DCC Live Validation — Archive", Guid.NewGuid().ToString("N"), cancellation.Token).ConfigureAwait(false);
            var archiveResult = await approvals.ArchiveAsync(archive.Id, cancellation.Token).ConfigureAwait(false);
            var history = await repository.QueryApprovalsAsync(new ScheduledApprovalQuery(null, profile.Id, server.Id, null, null, null, null, null, ScheduledApprovalSort.DecisionNewest, 1, 50) { HistoryOnly = true }, cancellation.Token).ConfigureAwait(false);
            var states = history.Items.Where(item => item.OccurrenceId is var id && (id == approve.Id || id == skip.Id || id == archive.Id)).Select(item => item.State).ToArray();
            if (!duplicateRejected || !skipResult || !archiveResult || !states.Contains(MessageOperationState.Delivered) || !states.Contains(MessageOperationState.Skipped) || !states.Contains(MessageOperationState.Archived))
            {
                return Fail("Atomic approval, skip/archive, or terminal-history validation failed.");
            }

            Console.WriteLine($"Live validation passed | Bot={profile.DisplayName} | Server=teast ({server.Id}) | Channel={ValidationChannelName} ({channel.Id}) {(createdChannelId is null ? "reused" : "created")} | ApprovedOccurrence={approve.Id} | MessageId={delivery.DiscordMessageId} | MatchingMessages={matchingMessages} | DuplicateRejected=True | Skip=Skipped | Archive=Archived | History=Delivered,Skipped,Archived");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Fail("Live validation timed out and stopped safely.");
        }
        catch (Exception exception)
        {
            var category = exception is SqliteException sqlite
                ? $"SqliteException ({sqlite.SqliteErrorCode})"
                : exception.GetType().Name;
            return Fail($"Live validation stopped safely: {category}.");
        }
        finally
        {
            if (profile is not null)
            {
                if (createdChannelId is ulong channelId)
                {
                    try
                    {
                        var current = explorer.GetSnapshot(profile.Id).Servers.SingleOrDefault(item => item.Id == createdChannelId)
                            ?? explorer.GetSnapshot(profile.Id).Servers.SingleOrDefault(item => item.Name == RunnerArguments.RequiredServerName);
                        if (current is not null)
                        {
                            var planner = services.GetRequiredService<IChannelOperationPlanner>();
                            var executor = services.GetRequiredService<IChannelOperationExecutor>();
                            var delete = planner.PlanDelete(new DeleteChannelsRequest(profile.Id, current.Id, [channelId], false, false, ImmutableArray<ulong>.Empty, "Live validation cleanup"));
                            if (delete.IsSuccess && delete.Plan is not null) await executor.ExecuteAsync(delete.Plan, null, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
                await connections.DisconnectAsync(profile.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(ApplicationPaths.ForCurrentUser());
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddSingleton<IBotProfileRepository, SqliteBotProfileRepository>();
        services.AddSingleton<ITokenProtector, WindowsTokenProtector>();
        services.AddSingleton<IDiscordBotClientFactory, DiscordBotClientFactory>();
        services.AddSingleton<IPermissionResolutionService, PermissionResolutionService>();
        services.AddSingleton<BotConnectionManager>();
        services.AddSingleton<IBotConnectionManager>(provider => provider.GetRequiredService<BotConnectionManager>());
        services.AddSingleton<IBotExplorerService>(provider => provider.GetRequiredService<BotConnectionManager>());
        services.AddSingleton<IScheduledMessageRepository, SqliteScheduledMessageRepository>();
        services.AddSingleton<IMessagePlanBuilder, MessagePlanBuilder>();
        services.AddSingleton<IMessagePreflightService, MessagePreflightService>();
        services.AddSingleton<IDiscordMessageWriter>(provider => provider.GetRequiredService<BotConnectionManager>());
        services.AddSingleton<ILiveValidationMessageVerifier>(provider => provider.GetRequiredService<BotConnectionManager>());
        services.AddSingleton<IDiscordChannelWriter>(provider => provider.GetRequiredService<BotConnectionManager>());
        services.AddSingleton<IDeliveryHistoryRepository, SqliteDeliveryHistoryRepository>();
        services.AddSingleton<IMessageDeliveryExecutor, MessageDeliveryExecutor>();
        services.AddSingleton<IScheduledApprovalPreflightService, ScheduledApprovalPreflightService>();
        services.AddSingleton<IScheduledApprovalService, ScheduledApprovalService>();
        services.AddSingleton<IRoleHierarchySafetyService, RoleHierarchySafetyService>();
        services.AddSingleton<IVoiceChannelValidationService, VoiceChannelValidationService>();
        services.AddSingleton<IChannelOperationPlanner, ChannelOperationPlanner>();
        services.AddSingleton<IChannelOperationPreflightService, ChannelOperationPreflightService>();
        services.AddSingleton<IOperationReconciliationService, ChannelOperationReconciliationService>();
        services.AddSingleton<IOperationHistoryRepository, SqliteOperationHistoryRepository>();
        services.AddSingleton<IOperationBackupRepository, SqliteOperationBackupRepository>();
        services.AddSingleton<IChannelOperationExecutor, ChannelOperationExecutor>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<ScheduledMessageOccurrence> ReserveAsync(IScheduledMessageRepository repository, Guid botProfileId, ServerReadModel server, ChannelReadModel channel, string name, string validationId, CancellationToken cancellationToken)
    {
        var definition = new ScheduledMessageDefinition(Guid.NewGuid(), botProfileId, MessageDestination.Channel(server.Id, "teast", channel.Id, ValidationChannelName), null, new MessageContent($"Discord Control Center live approval validation — {validationId}", null, AllowedMentionPolicy.None), ScheduledMessageRecurrence.Once, TimeOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc.Id, ImmutableArray<DayOfWeek>.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, MissedOccurrencePolicy.RequireManualApproval, 0, null, null) { Name = name };
        await repository.SaveAsync(definition, cancellationToken).ConfigureAwait(false);
        var snapshot = JsonSerializer.Serialize(new ScheduledDeliverySnapshot(1, definition, definition.InlineContent!, null, null, DateTimeOffset.UtcNow));
        var occurrence = new ScheduledMessageOccurrence(Guid.NewGuid(), definition.Id, DateTimeOffset.UtcNow, MessageOperationState.PendingApproval, Guid.NewGuid(), null, null) { ImmutableDeliverySnapshotJson = snapshot, ManualDecision = "Live validation reservation" };
        if (!await repository.TryReserveOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Occurrence reservation was rejected.");
        return occurrence;
    }

    private static bool IsExpectedPreflight(ScheduledApprovalPreflightResult result) => result.CanSend && result.Checks.Count == 14 && result.Checks[11].State == ScheduledApprovalPreflightState.NotRequired && result.Checks[12].State == ScheduledApprovalPreflightState.NotRequired && result.Checks[13].State == ScheduledApprovalPreflightState.NotRequired;
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
