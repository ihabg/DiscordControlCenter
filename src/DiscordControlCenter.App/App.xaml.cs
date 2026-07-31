using System.IO;
using System.Windows;
using System.Windows.Threading;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Core.Auditing;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Persistence;
using DiscordControlCenter.Core.Security;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Core.Messaging;
using DiscordControlCenter.Discord;
using DiscordControlCenter.Infrastructure.Configuration;
using DiscordControlCenter.Infrastructure.Persistence;
using DiscordControlCenter.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Json;

namespace DiscordControlCenter.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Serilog.Core.Logger? _bootstrapLogger;
    private int _handlingFatalUiException;
    private int _fatalShutdownRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterExceptionHandlers();
        var paths = ApplicationPaths.ForCurrentUser();
        Directory.CreateDirectory(paths.LogDirectory);
        _bootstrapLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                new JsonFormatter(),
                Path.Combine(paths.LogDirectory, "control-center-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();

        try
        {
            _host = Host.CreateDefaultBuilder()
                .UseSerilog(_bootstrapLogger, dispose: false)
                .ConfigureServices(
                    services =>
                    {
                        services.AddSingleton(paths);
                        services.AddSingleton(new UiDispatcher(Dispatcher));
                        services.AddSingleton<IClock, SystemClock>();
                        services.AddSingleton<SqliteConnectionFactory>();
                        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
                        services.AddSingleton<IBotProfileRepository, SqliteBotProfileRepository>();
                        services.AddSingleton<IAuditRepository, SqliteAuditRepository>();
                        services.AddSingleton<IOperationHistoryRepository, SqliteOperationHistoryRepository>();
                        services.AddSingleton<IOperationBackupRepository, SqliteOperationBackupRepository>();
                        services.AddSingleton<SqliteOperationalRecoveryRepository>();
                        services.AddSingleton<IOperationHistoryQueryRepository>(
                            provider => provider.GetRequiredService<SqliteOperationalRecoveryRepository>());
                        services.AddSingleton<IBackupCatalogRepository>(
                            provider => provider.GetRequiredService<SqliteOperationalRecoveryRepository>());
                        services.AddSingleton<IManualReconciliationRepository>(
                            provider => provider.GetRequiredService<SqliteOperationalRecoveryRepository>());
                        services.AddSingleton<ITokenProtector, WindowsTokenProtector>();
                        services.AddSingleton<IDiscordTokenValidator, DiscordTokenValidator>();
                        services.AddSingleton<IDiscordBotClientFactory, DiscordBotClientFactory>();
                        services.AddSingleton<BotConnectionManager>();
                        services.AddSingleton<IBotConnectionManager>(
                            provider => provider.GetRequiredService<BotConnectionManager>());
                        services.AddSingleton<IBotExplorerService>(
                            provider => provider.GetRequiredService<BotConnectionManager>());
                        services.AddSingleton<IDiscordChannelWriter>(
                            provider => provider.GetRequiredService<BotConnectionManager>());
                        services.AddSingleton<IPermissionResolutionService, PermissionResolutionService>();
                        services.AddSingleton<IRoleHierarchySafetyService, RoleHierarchySafetyService>();
                        services.AddSingleton<IVoiceChannelValidationService, VoiceChannelValidationService>();
                        services.AddSingleton<IChannelOperationPlanner, ChannelOperationPlanner>();
                        services.AddSingleton<IBackupCatalogService, BackupCatalogService>();
                        services.AddSingleton<IRecreateStructurePlanner, RecreateStructurePlanner>();
                        services.AddSingleton<IOperationRecoveryService, OperationRecoveryService>();
                        services.AddSingleton<IOperationExportService, OperationExportService>();
                        services.AddSingleton<IChannelOperationPreflightService, ChannelOperationPreflightService>();
                        services.AddSingleton<IOperationReconciliationService, ChannelOperationReconciliationService>();
                        services.AddSingleton<IChannelOperationExecutor, ChannelOperationExecutor>();
                        services.AddSingleton<ChannelOperationScheduler>();
                        services.AddSingleton<IChannelOperationScheduler>(
                            provider => provider.GetRequiredService<ChannelOperationScheduler>());
                        services.AddSingleton<IMessageTemplateRepository, SqliteMessageTemplateRepository>();
                        services.AddSingleton<IAutomationRuleRepository, SqliteAutomationRuleRepository>();
                        services.AddSingleton<IAutomationExecutionRepository, SqliteAutomationExecutionRepository>();
                        services.AddSingleton<IDeliveryHistoryRepository, SqliteDeliveryHistoryRepository>();
                        services.AddSingleton<IMessagePlanBuilder, MessagePlanBuilder>();
                        services.AddSingleton<ITemplateRenderer, TemplateRenderer>();
                        services.AddSingleton<IMessagePreflightService, MessagePreflightService>();
                        services.AddSingleton<IScheduledApprovalPreflightService, ScheduledApprovalPreflightService>();
                        services.AddSingleton<IDiscordMessageWriter>(
                            provider => provider.GetRequiredService<BotConnectionManager>());
                        services.AddSingleton<IMessageDeliveryExecutor, MessageDeliveryExecutor>();
                        services.AddSingleton<IMessageDeliveryDialogService, MessageDeliveryDialogService>();
                        services.AddSingleton<IScheduledMessageService, ScheduledMessageService>();
                        services.AddSingleton<IScheduledMessageRepository, SqliteScheduledMessageRepository>();
                        services.AddSingleton<IScheduledMessageScheduler, ScheduledMessageScheduler>();
                        services.AddSingleton<IScheduledApprovalService, ScheduledApprovalService>();
                        services.AddSingleton<IAutomationRulePreflightService, AutomationRulePreflightService>();
                        services.AddSingleton<IBotProfileService, BotProfileService>();
                        services.AddSingleton<IUserDialogService, WpfUserDialogService>();
                        services.AddSingleton<IChannelOperationDialogService, ChannelOperationDialogService>();
                        services.AddSingleton<IOperationPlanSubmissionService, OperationPlanSubmissionService>();
                        services.AddSingleton<DashboardViewModel>();
                        services.AddSingleton<BotsViewModel>();
                        services.AddSingleton<ServersViewModel>();
                        services.AddSingleton<ChannelsViewModel>();
                        services.AddSingleton<MembersViewModel>();
                        services.AddSingleton<RolesViewModel>();
                        services.AddSingleton<PermissionSimulatorViewModel>();
                        services.AddSingleton<VoiceViewModel>();
                        services.AddSingleton<OperationCenterViewModel>();
                        services.AddSingleton<BackupBrowserViewModel>();
                        services.AddSingleton<MessagesViewModel>();
                        services.AddSingleton<AutomationViewModel>();
                        services.AddSingleton<MainWindowViewModel>();
                        services.AddSingleton<MainWindow>();
                    })
                .Build();

            await _host.StartAsync();
            await _host.Services
                .GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
            await _host.Services
                .GetRequiredService<IBotConnectionManager>()
                .InitializeAsync(CancellationToken.None);
            await _host.Services
                .GetRequiredService<IOperationRecoveryService>()
                .InspectInterruptedAsync(CancellationToken.None);
            await _host.Services
                .GetRequiredService<IChannelOperationScheduler>()
                .InitializeAsync(CancellationToken.None);
            await _host.Services
                .GetRequiredService<IScheduledMessageScheduler>()
                .InitializeAsync(CancellationToken.None);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            LogFatalWithoutMessage(exception, "Application startup failed");
            MessageBox.Show(
                "Discord Control Center could not start. Review the local structured log for details.",
                "Startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterExceptionHandlers();
        if (_host is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            _host?.Dispose();
        }

        _bootstrapLogger?.Dispose();
        base.OnExit(e);
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnregisterExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        if (Interlocked.CompareExchange(ref _handlingFatalUiException, 1, 0) != 0)
        {
            RequestFatalShutdown();
            return;
        }

        LogFatalWithoutMessage(e.Exception, "Unhandled UI exception");
        try
        {
            MessageBox.Show(
                "Discord Control Center encountered an unexpected error and must close. Details were written to the local log.",
                "Unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception dialogException)
        {
            LogFatalWithoutMessage(dialogException, "Fatal error dialog failed");
        }
        finally
        {
            RequestFatalShutdown();
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _ = sender;
        if (e.ExceptionObject is Exception exception)
        {
            LogFatalWithoutMessage(exception, "Unhandled application exception");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _ = sender;
        LogFatalWithoutMessage(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void LogFatalWithoutMessage(Exception exception, string context)
    {
        try
        {
            var logger = _host?.Services.GetService<ILogger<App>>();
            if (logger is not null)
            {
                FatalLog(
                    logger,
                    context,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.StackTrace ?? "Unavailable",
                    null);
                return;
            }

            _bootstrapLogger?.Fatal(
                "{Context}. ExceptionType {ExceptionType}. StackTrace {StackTrace}",
                context,
                exception.GetType().FullName,
                exception.StackTrace);
        }
        catch
        {
            // Exception reporting must never cause another unhandled exception.
        }
    }

    private void RequestFatalShutdown()
    {
        if (Interlocked.CompareExchange(ref _fatalShutdownRequested, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => Shutdown(1)));
        }
        catch
        {
            Environment.ExitCode = 1;
        }
    }

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, string, string, string, Exception?>
        FatalLog = LoggerMessage.Define<string, string, string>(
            LogLevel.Critical,
            new EventId(4001, nameof(FatalLog)),
            "{Context}. ExceptionType {ExceptionType}. StackTrace {StackTrace}");
}
