using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// Wave F fix loop 2: content strings are bounded (they travel in inventory snapshots) and keys and
// ids hold no control characters; an enemy must be able to move.
public sealed class ContentStringBoundsTests
{
    private static string ShippedItemsJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName));

    private static string ShippedLootJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName));

    private static string ShippedEnemiesJson => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.EnemiesFileName));

    private static ContentValidationException ItemsFail(string items) =>
        Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(items, ShippedEnemiesJson, ShippedLootJson));

    private static string WithItemField(string field, string oldValue, string newJsonValue)
    {
        string items = ShippedItemsJson;
        int at = items.IndexOf($"\"{field}\": \"{oldValue}", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{field} {oldValue} not found");
        int valueStart = items.IndexOf('"', at + field.Length + 3);
        int valueEnd = items.IndexOf('"', valueStart + 1);
        return string.Concat(items.AsSpan(0, valueStart), newJsonValue, items.AsSpan(valueEnd + 1));
    }

    [Fact]
    public void TheShippedContent_StaysValid_UnderTheBounds()
    {
        ContentCatalog catalog = ContentCatalog.Load(ShippedItemsJson, ShippedEnemiesJson, ShippedLootJson);
        Assert.NotEmpty(catalog.Items);
    }

    [Fact]
    public void AnItemKey_WithANulCharacter_IsRejected()
    {
        string items = WithItemField("key", "tool.field-blade", "\"tool.field\\u0000blade\"");
        Assert.Contains("control characters", ItemsFail(items).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnemyKey_WithAControlCharacter_IsRejected()
    {
        string enemies = ShippedEnemiesJson.Replace("\"key\": \"Wolf\"", "\"key\": \"Wo\\u0007lf\"");
        ContentValidationException ex = Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));
        Assert.Contains("control characters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKey_LongerThanTheLimit_IsRejected()
    {
        string items = ShippedItemsJson.Replace("tool.field-blade", new string('k', ContentCatalog.MaxKeyLength + 1), StringComparison.Ordinal);
        Assert.Contains("characters long", ItemsFail(items).Message, StringComparison.Ordinal);

        string atLimit = ShippedItemsJson.Replace("tool.field-blade", new string('k', ContentCatalog.MaxKeyLength), StringComparison.Ordinal);
        Assert.NotNull(ContentCatalog.Load(atLimit, ShippedEnemiesJson, ShippedLootJson));
    }

    [Fact]
    public void AFiveMegabyteDisplayName_IsRejected()
    {
        string items = WithItemField("displayName", "Builder Hammer", $"\"{new string('N', 5 * 1024 * 1024)}\"");
        Assert.Contains("characters long", ItemsFail(items).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisplayName_WithAControlCharacterOrALineBreak_IsRejected()
    {
        Assert.Contains("control characters", ItemsFail(WithItemField("displayName", "Builder Hammer", "\"Ham\\u0000mer\"")).Message, StringComparison.Ordinal);
        Assert.Contains("single line", ItemsFail(WithItemField("displayName", "Builder Hammer", "\"Ham\\nmer\"")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADescription_LongerThanTheLimit_IsRejected()
    {
        string items = WithItemField("description", "Reliable", $"\"{new string('d', ContentCatalog.MaxDescriptionLength + 1)}\"");
        Assert.Contains("characters long", ItemsFail(items).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Held.Tool")]
    [InlineData("held tool")]
    [InlineData(".held")]
    [InlineData("held.")]
    [InlineData("held\\u0000x")]
    [InlineData("held/../x")]
    public void AMalformedVisualId_IsRejected(string visualId)
    {
        string items = WithItemField("visualId", "held.tool.builder", $"\"{visualId}\"");
        Assert.Contains("not a visual id", ItemsFail(items).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-0.0")]
    [InlineData("0")]
    [InlineData("1e-300")]
    [InlineData("0.001")]
    [InlineData("-1")]
    public void AMoveSpeedBelowTheMinimum_IsRejected(string speed)
    {
        string enemies = ShippedEnemiesJson.Replace("\"moveSpeed\": 5.0", $"\"moveSpeed\": {speed}");
        Assert.NotEqual(ShippedEnemiesJson, enemies);
        ContentValidationException ex = Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));
        Assert.Contains("moveSpeed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMinimumMoveSpeed_IsAccepted()
    {
        // The patrol walk goes down with it: it can never exceed the chase speed.
        string enemies = ShippedEnemiesJson
            .Replace("\"moveSpeed\": 5.0", "\"moveSpeed\": 0.01")
            .Replace("\"patrolSpeed\": 1.4", "\"patrolSpeed\": 0.01");
        Assert.NotNull(ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));
    }

    [Theory]
    [InlineData("-0.0")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1e-300")]
    [InlineData("101")]
    public void APatrolSpeedOutsideTheRange_IsRejected(string speed)
    {
        string enemies = ShippedEnemiesJson.Replace("\"patrolSpeed\": 1.4", $"\"patrolSpeed\": {speed}");
        Assert.NotEqual(ShippedEnemiesJson, enemies);
        ContentValidationException ex = Assert.Throws<ContentValidationException>(() => ContentCatalog.Load(ShippedItemsJson, enemies, ShippedLootJson));
        Assert.Contains("patrolSpeed", ex.Message, StringComparison.Ordinal);
    }
}
