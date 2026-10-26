using System.Security.Cryptography;
using System.Text;

namespace Tia.Core.Infrastructure;

public static class Hashing
{
    /// <summary>The first <paramref name="length"/> lowercase hex digits of the SHA-256 of <paramref name="value"/> as UTF-8.</summary>
    public static string Sha256Hex(string value, int length) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length];
}
