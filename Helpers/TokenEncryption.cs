using System.Security.Cryptography;
using System.Text;

namespace FamilyGuardian.Api.Helpers;

public static class TokenEncryption
{
    public static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
