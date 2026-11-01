using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// Wave F fix loop: content keys are exact (no surrounding whitespace, "Wolf" in that case), and the
// digest cannot be fooled by a separator inside a value.
public sealed class ContentKeyRulesTests
{
    private static string ShippedItemsJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));

    private static string ShippedLootJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName));

    private static string ShippedEnemiesJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.EnemiesFileName));

    [Fact]
    public void AnItemKey_WithSurroundingWhitespace_IsRejected()
    {
        string items = ShippedItemsJson.Replace("\"key\": \"tool.field-blade\"", "\"key\": \" tool.field-blade\"");

        ContentValidationException ex = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(items, ShippedEnemiesJson, ShippedLootJson));

        Assert.Contains("leading or trailing whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnemyKey_WithSurroundingWhitespace_IsRejected()
    {
        string enemies = ShippedEnemiesJson.Replace("\"key\": \"Wolf\"", "\"key\": \"Wolf \"");

        ContentValidationException ex = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));

        Assert.Contains("leading or trailing whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWolfKey_MustBeSpelledExactly()
    {
        string enemies = ShippedEnemiesJson.Replace("\"key\": \"Wolf\"", "\"key\": \"wolf\"");

        ContentValidationException ex = Assert.Throws<ContentValidationException>(
            () => ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));

        Assert.Contains("case-sensitive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDigest_SeparatesFieldsByLength_SoASeparatorInsideAValueDoesNotAlias()
    {
        // "A|B" + "C" against "A" + "B|C": a '|'-joined digest hashes both alike.
        string items = ShippedItemsJson;
        string original = "\"displayName\": \"Builder Hammer\",\r\n      \"description\": \"Reliable";
        if (!items.Contains(original, StringComparison.Ordinal))
        {
            original = original.Replace("\r\n", "\n");
        }

        Assert.Contains(original, items, StringComparison.Ordinal);
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string first = items.Replace(
            original, $"\"displayName\": \"A|B\",{newline}      \"description\": \"C Reliable");
        string second = items.Replace(
            original, $"\"displayName\": \"A\",{newline}      \"description\": \"B|C Reliable");
        Assert.NotEqual(
            ContentCatalog.Load(first, ShippedEnemiesJson, ShippedLootJson).ContentDigest,
            ContentCatalog.Load(second, ShippedEnemiesJson, ShippedLootJson).ContentDigest);
    }
}
