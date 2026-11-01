using System.Globalization;
using System.Text.RegularExpressions;
using Dream.Simulation.Characters;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Simulation.Characters;

// Wave G C0: the name rule for new characters, the key type, and the NOCASE comparer.
public sealed partial class CharacterNamesTests
{
    // Test data spells awkward characters as {uXXXX} or {uXXXXXX} (a code point), so the source stays plain.
    [GeneratedRegex(@"\{u([0-9A-Fa-f]{4,6})\}")]
    private static partial Regex EscapePattern();

    private static string Decode(string spelled) =>
        EscapePattern().Replace(spelled, match =>
        {
            int codePoint = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            // A lone surrogate cannot go through ConvertFromUtf32.
            return codePoint is >= 0xD800 and <= 0xDFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);
        });

    [Theory]
    [InlineData("a", true)]
    [InlineData("Bob Jones", true)]
    [InlineData("{u00C4}rger {u00DC}nder", true)]
    [InlineData("{u65E5}{u672C}{u8A9E}", true)]
    [InlineData("name with {u1F600} emoji", true)]
    [InlineData("", false)]
    [InlineData("12345678901234567890123456789012", true)]
    [InlineData("123456789012345678901234567890123", false)]
    // Control, tab, newline, delete, next line.
    [InlineData("a{u0001}b", false)]
    [InlineData("a{u0009}b", false)]
    [InlineData("a{u000A}b", false)]
    [InlineData("a{u007F}b", false)]
    [InlineData("a{u0085}b", false)]
    // Format: zero width space, joiner, bidi override and isolate, BOM, soft hyphen.
    [InlineData("a{u200B}b", false)]
    [InlineData("a{u200D}b", false)]
    [InlineData("a{u202E}b", false)]
    [InlineData("a{u2066}b", false)]
    [InlineData("{uFEFF}ab", false)]
    [InlineData("a{u00AD}b", false)]
    // Line and paragraph separators.
    [InlineData("a{u2028}b", false)]
    [InlineData("a{u2029}b", false)]
    // Private use (BMP).
    [InlineData("a{uE000}b", false)]
    // Unpaired surrogates.
    [InlineData("a{uD800}", false)]
    [InlineData("{uDC00}a", false)]
    [InlineData("a{uD800}b", false)]
    public void IsValidNewName_AppliesTheRule(string spelled, bool expected)
    {
        Assert.Equal(expected, CharacterNames.IsValidNewName(Decode(spelled)));
    }

    [Fact]
    public void IsValidNewName_CountsUtf16CodeUnits_AndRefusesPrivateUseSurrogatePairs()
    {
        // 16 pairs are 32 code units; one more unit is 33.
        string sixteenEmoji = string.Concat(Enumerable.Repeat(Decode("{u1F600}"), 16));
        Assert.Equal(32, sixteenEmoji.Length);
        Assert.True(CharacterNames.IsValidNewName(sixteenEmoji));
        Assert.False(CharacterNames.IsValidNewName(sixteenEmoji + "a"));
        // U+F0000 is plane 15 private use.
        Assert.False(CharacterNames.IsValidNewName("a" + Decode("{uF0000}")));
        Assert.False(CharacterNames.IsValidNewName(null!));
    }

    [Fact]
    public void Constants_MatchThePlan()
    {
        Assert.Equal(32, CharacterNames.MaxNewNameLength);
        Assert.Equal(5, CharacterNames.MaxCharactersPerAccount);
        Assert.Equal("a b", CharacterNames.Normalize("  a b\t"));
    }

    [Fact]
    public void Comparer_FoldsAsciiCaseOnly()
    {
        IEqualityComparer<string> comparer = CharacterNames.Comparer;

        Assert.True(comparer.Equals("Bob", "bOB"));
        Assert.False(comparer.Equals("Bob", "Bobb"));
        Assert.False(comparer.Equals(Decode("{u00C4}b"), Decode("{u00E4}b")));
        Assert.Equal(comparer.GetHashCode("Bob"), comparer.GetHashCode("bOB"));
        Assert.False(comparer.Equals("a", null));
    }

    [Fact]
    public void Comparer_AgreesWithSqliteNocase_OnASampleWithNonAscii()
    {
        string[] sample =
        [
            "Bob", "bob", "BOB", "bobby", "{u00C4}b", "{u00E4}b", "{u00C4}B", "stra{u00DF}e", "STRASSE", "{u0130}", "i",
            "{u0131}", "I", "{u03A9}", "{u03C9}", "{u65E5}{u672C}", "{u1F600}", "a~2", "A~2", "", "[", "{", "@", "`", "Z", "z"
        ];
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT $a = $b COLLATE NOCASE;";
        SqliteParameter a = command.Parameters.Add("$a", SqliteType.Text);
        SqliteParameter b = command.Parameters.Add("$b", SqliteType.Text);

        foreach (string spelledLeft in sample)
        {
            foreach (string spelledRight in sample)
            {
                string left = Decode(spelledLeft);
                string right = Decode(spelledRight);
                a.Value = left;
                b.Value = right;
                bool sqlite = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
                Assert.True(
                    sqlite == CharacterNames.Comparer.Equals(left, right),
                    $"'{spelledLeft}' vs '{spelledRight}': sqlite={sqlite}");
            }
        }
    }

    [Fact]
    public void CharacterKey_IsOrdinal_AndRefusesBlank()
    {
        Assert.Equal(new CharacterKey("Bob"), new CharacterKey("Bob"));
        Assert.NotEqual(new CharacterKey("Bob"), new CharacterKey("bob"));
        Assert.Equal("Bob", new CharacterKey("Bob").ToString());
        Assert.Throws<ArgumentException>(() => new CharacterKey(" "));
        Assert.Throws<ArgumentNullException>(() => new CharacterKey(null!));
        Assert.True(default(CharacterKey).IsNone);
        Assert.Equal(string.Empty, default(CharacterKey).ToString());
        Assert.False(new CharacterKey("Bob").IsNone);
    }
}
