namespace DiscordControlCenter.Core.Security;

public interface ITokenProtector
{
    byte[] Protect(string token);
    string Unprotect(byte[] protectedToken);
    string CreateFingerprint(string token);
}
