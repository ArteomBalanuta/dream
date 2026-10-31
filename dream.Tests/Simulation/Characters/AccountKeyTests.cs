using Dream.Simulation.Characters;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.Simulation.Characters;

// Wave G C0: the strict token form and the hash.
public sealed class AccountKeyTests
{
    private const string ZeroToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ZeroTokenSha256 = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";

    [Fact]
    public void AccountKey_ValidToken_GivesTheGoldenHash()
    {
        Assert.True(AccountKey.TryFromToken(ZeroToken, out AccountKey key));

        Assert.Equal(ZeroTokenSha256, key.HashHex);
        Assert.Equal(Convert.FromHexString(ZeroTokenSha256), key.ToHashBytes());
        Assert.Equal("account", key.ToString());
        Assert.DoesNotContain(ZeroToken, key.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AccountKey_FromHash_RoundTripsAndEqualsTheTokenKey()
    {
        Assert.True(AccountKey.TryFromToken(ZeroToken, out AccountKey fromToken));

        AccountKey fromHash = AccountKey.FromHash(Convert.FromHexString(ZeroTokenSha256));

        Assert.Equal(fromToken, fromHash);
        Assert.Throws<ArgumentException>(() => AccountKey.FromHash(new byte[31]));
    }

    [Fact]
    public void AccountKey_TestTokens_AreValidAndStablePerName()
    {
        string token = TestAccountTokens.For("Ann");

        Assert.Equal(AccountKey.TokenChars, token.Length);
        Assert.Equal(token, TestAccountTokens.For("Ann"));
        Assert.NotEqual(token, TestAccountTokens.For("Bob"));
        Assert.True(AccountKey.TryFromToken(token, out AccountKey key));
        Assert.Equal(key, TestAccountTokens.KeyFor("Ann"));
    }

    public static TheoryData<string?> BadTokens => new()
    {
        null,
        "",
        "   ",
        ZeroToken[..42],
        ZeroToken + "A",
        // Padding in place of the last character, and the 44 character padded form.
        ZeroToken[..42] + "=",
        ZeroToken + "=",
        // Standard base64 alphabet instead of base64url.
        "+" + ZeroToken[1..],
        "/" + ZeroToken[1..],
        // Non-canonical last character: its low two bits are not zero.
        ZeroToken[..42] + "B",
        ZeroToken[..42] + "_",
        // Whitespace anywhere, including a trailing newline that keeps 43 characters.
        ZeroToken[..42] + "\n",
        " " + ZeroToken[1..],
        ZeroToken[..20] + " " + ZeroToken[21..],
        "\t" + ZeroToken[1..],
        // Not ASCII.
        "é" + ZeroToken[1..]
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public void AccountKey_MalformedToken_IsRefused(string? token)
    {
        Assert.False(AccountKey.TryFromToken(token, out AccountKey key));
        Assert.Equal(default, key);
    }

    [Fact]
    public void AccountKey_CanonicalLastCharacters_AreAccepted()
    {
        // The last character carries 4 data bits, so its alphabet index must divide by 4 (A, E, Q, g, w ...).
        foreach (char last in "AQgw")
        {
            Assert.True(AccountKey.TryFromToken(ZeroToken[..42] + last, out _), last.ToString());
        }
    }
}
