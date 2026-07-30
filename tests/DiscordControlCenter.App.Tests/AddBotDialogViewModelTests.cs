using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;

namespace DiscordControlCenter.App.Tests;

public sealed class AddBotDialogViewModelTests
{
    [Fact]
    public void RequiredFieldValidationIsSpecificAndClearsAsValuesBecomeValid()
    {
        using var viewModel = new AddBotDialogViewModel(new ControlledBotProfileService());

        viewModel.ValidateDisplayName();
        viewModel.ValidateToken();

        Assert.Equal("Display name is required.", viewModel.DisplayNameError);
        Assert.Equal("Bot token is required.", viewModel.TokenError);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        viewModel.DisplayName = "Operations bot";
        viewModel.Token = "token-without-whitespace";

        Assert.Null(viewModel.DisplayNameError);
        Assert.Null(viewModel.TokenError);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SavingShowsProgressAndPreventsDuplicateSubmission()
    {
        var service = new ControlledBotProfileService();
        using var viewModel = new AddBotDialogViewModel(service)
        {
            DisplayName = "Operations bot",
            Token = "token-without-whitespace"
        };
        var accepted = false;
        viewModel.RequestClose += (_, result) => accepted = result;

        var firstExecution = viewModel.SaveCommand.ExecuteAsync(null);
        await service.AddEntered.Task;

        Assert.True(viewModel.IsSaving);
        Assert.False(viewModel.CanCancel);
        Assert.Equal("Validating…", viewModel.SaveButtonText);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, service.AddCallCount);

        service.AddCompletion.SetResult(
            OperationResult.Success(
                new BotProfile(
                    Guid.NewGuid(),
                    "Operations bot",
                    [1, 2, 3],
                    "fingerprint",
                    true,
                    DateTimeOffset.UtcNow)));
        await firstExecution;

        Assert.True(accepted);
        Assert.False(viewModel.IsSaving);
        Assert.True(viewModel.CanCancel);
        Assert.Equal("Validate and save", viewModel.SaveButtonText);
        Assert.Equal(1, service.AddCallCount);
    }

    private sealed class ControlledBotProfileService : IBotProfileService
    {
        public TaskCompletionSource AddEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<OperationResult<BotProfile>> AddCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int AddCallCount { get; private set; }

        public Task<IReadOnlyList<BotProfile>> GetAllAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<BotProfile>>([]);
        }

        public Task<OperationResult<BotProfile>> AddAsync(
            AddBotRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            AddCallCount++;
            AddEntered.TrySetResult();
            return AddCompletion.Task;
        }

        public Task<OperationResult<BotProfile>> ReplaceTokenAsync(
            Guid botProfileId,
            string newToken,
            CancellationToken cancellationToken)
        {
            _ = botProfileId;
            _ = newToken;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<OperationResult<BotProfile>> SetFullMemberAccessAsync(
            Guid botProfileId,
            bool enabled,
            CancellationToken cancellationToken)
        {
            _ = botProfileId;
            _ = enabled;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<OperationResult> RemoveAsync(
            Guid botProfileId,
            CancellationToken cancellationToken)
        {
            _ = botProfileId;
            _ = cancellationToken;
            throw new NotSupportedException();
        }
    }
}
