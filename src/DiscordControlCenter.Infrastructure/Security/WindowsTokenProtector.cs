using System.Security.Cryptography;
using System.Text;
using DiscordControlCenter.Core.Security;

namespace DiscordControlCenter.Infrastructure.Security;

public sealed class WindowsTokenProtector : ITokenProtector
{
    private static readonly byte[] Entropy =
        "DiscordControlCenter.BotToken.v1"u8.ToArray();

    public byte[] Protect(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var plaintext = Encoding.UTF8.GetBytes(token);
        try
        {
            return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(byte[] protectedToken)
    {
        ArgumentNullException.ThrowIfNull(protectedToken);
        var plaintext = ProtectedData.Unprotect(
            protectedToken,
            Entropy,
            DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string CreateFingerprint(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(tokenBytes, digest);
            return Convert.ToHexString(digest[..6]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }
}
