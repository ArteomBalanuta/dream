using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dream.Simulation.Characters;

namespace Dream.Tests.Support;

// A stable, valid account token derived from a name's SHA-256, so a restarted test keeps its account.
internal static class TestAccountTokens
{
    public static string For(string name)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes("test-account:" + name));
        return Base64Url.EncodeToString(bytes);
    }

    public static AccountKey KeyFor(string name)
    {
        if (!AccountKey.TryFromToken(For(name), out AccountKey key))
        {
            throw new InvalidOperationException("A derived test token must be valid.");
        }

        return key;
    }
}
