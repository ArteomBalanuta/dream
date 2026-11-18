using System.Text.Json.Nodes;
using Dream.Simulation.Content;

namespace Dream.Tests.BugHunt;

// Shared helpers for the BUGHUNTER-CONTENT validation tests (BH-CONTENT-1..6): load the shipped
// content with one file swapped, so every test states only the thing it changes.
internal static class ContentBugHunt
{
    public static string ShippedFile(string file) => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    // The shipped catalog with any one of its files replaced. A null argument keeps the shipped file.
    public static ContentCatalog Load(
        string? itemsJson = null,
        string? npcsJson = null,
        string? questsJson = null) =>
        ContentCatalog.Load(
            itemsJson ?? ShippedFile(ContentCatalog.ItemsFileName),
            ShippedFile(ContentCatalog.EnemiesFileName),
            ShippedFile(ContentCatalog.LootFileName),
            ShippedFile(ContentCatalog.SpawnsFileName),
            npcsJson ?? ShippedFile(ContentCatalog.NpcsFileName),
            ShippedFile(ContentCatalog.SkillsFileName),
            questsJson ?? ShippedFile(ContentCatalog.QuestsFileName));

    // The shipped items.json with one property of one item replaced (a JSON value, parsed).
    public static string ItemsWith(string itemKey, string property, JsonNode? value)
    {
        JsonNode root = JsonNode.Parse(ShippedFile(ContentCatalog.ItemsFileName))!;
        JsonNode item = root["items"]!.AsArray().First(node => (string?)node!["key"] == itemKey)!;
        item[property] = value;
        return root.ToJsonString();
    }

    // The shipped items.json with a "use" field set on one consumable.
    public static string ItemsWithUseField(string itemKey, string field, JsonNode? value)
    {
        JsonNode root = JsonNode.Parse(ShippedFile(ContentCatalog.ItemsFileName))!;
        JsonNode item = root["items"]!.AsArray().First(node => (string?)node!["key"] == itemKey)!;
        item["use"]![field] = value;
        return root.ToJsonString();
    }

    public static string VendorStockKey(int n) => "vendor.filler-" + n;

    // The shipped items plus `count` fixed-stat copies of potion.health, one per stock entry, so a
    // vendor may be stocked past the shipped catalog's seven unrolled items. A vendor sells fixed-stat
    // items only, which is why the copies are of a potion and not of a rolled tool.
    public static string ItemsWithVendorFillers(int count)
    {
        JsonNode root = JsonNode.Parse(ShippedFile(ContentCatalog.ItemsFileName))!;
        JsonArray items = root["items"]!.AsArray();
        JsonNode template = items.First(node => (string?)node!["key"] == "potion.health")!;
        for (int n = 0; n < count; n++)
        {
            JsonNode copy = JsonNode.Parse(template.ToJsonString())!;
            copy["key"] = VendorStockKey(n);
            copy["displayName"] = "Filler " + n;
            items.Add(copy);
        }

        return root.ToJsonString();
    }

    // npcs.json holding one vendor with the given number of stock entries. Every entry names a real
    // item at a price above its sell price, so only the count is ever the reason to fail.
    public static string VendorWithStock(int count)
    {
        string entries = string.Join(",", Enumerable.Range(0, count).Select(n => $"{{\"item\":\"{VendorStockKey(n)}\",\"price\":{100 + n}}}"));
        return "{\"version\":1,\"npcs\":[{\"key\":\"spawn-quartermaster\",\"displayName\":\"Quartermaster\","
            + "\"visualKey\":\"npc.vendor\",\"offsetX\":4,\"offsetZ\":2,\"stock\":[" + entries + "]},"
            + "{\"key\":\"gate-warden\",\"displayName\":\"Gate Warden\",\"visualKey\":\"npc.warden\","
            + "\"offsetX\":-4,\"offsetZ\":-5,\"stock\":[]}]}";
    }
}
