namespace DiscordControlCenter.Core.Common;

public record OperationResult(bool IsSuccess, string? Error)
{
    public static OperationResult Success() => new(true, null);
    public static OperationResult Failure(string error) => new(false, error);
    public static OperationResult<T> Success<T>(T value) => new(true, value, null);
    public static OperationResult<T> Failure<T>(string error) => new(false, default, error);
}

public sealed record OperationResult<T>(bool IsSuccess, T? Value, string? Error);
