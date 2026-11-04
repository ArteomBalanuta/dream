using System.Text.Json;
using Dream.Assets;
using Dream.Rendering.Core;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Ground;

// The ground assets described by the manifests under dream/animations/ground (flora.json, stone.json, decals.json).
// A missing directory or manifest is an empty set (the scatter then draws nothing); an entry that cannot be used
// (unknown class, layer or material, a decal on the wrong layer, a bad number, a repeated id) is skipped and
// reported to the render fault log. Loading never throws.
public sealed class GroundAssetSet
{
    public const string FaultSource = "ground-assets";
    public const string Schema = "dream.ground/1";

    public static readonly GroundAssetSet Empty = new([]);

    private static readonly Dictionary<string, GroundAssetClass> Classes = new(StringComparer.Ordinal)
    {
        ["tree"] = GroundAssetClass.Tree,
        ["sapling"] = GroundAssetClass.Sapling,
        ["bush"] = GroundAssetClass.Bush,
        ["grass"] = GroundAssetClass.Grass,
        ["flower"] = GroundAssetClass.Flower,
        ["mushroom"] = GroundAssetClass.Mushroom,
        ["root"] = GroundAssetClass.Root,
        ["rock"] = GroundAssetClass.Rock,
        ["pebbles"] = GroundAssetClass.Pebbles,
        ["stump"] = GroundAssetClass.Stump,
        ["log"] = GroundAssetClass.Log,
        ["twigs"] = GroundAssetClass.Twigs,
        ["leaves"] = GroundAssetClass.Leaves,
        ["bones"] = GroundAssetClass.Bones,
        ["item"] = GroundAssetClass.Item,
        ["decal.dirt"] = GroundAssetClass.DecalDirt,
        ["decal.moss"] = GroundAssetClass.DecalMoss,
        ["decal.puddle"] = GroundAssetClass.DecalPuddle,
        ["decal.crack"] = GroundAssetClass.DecalCrack,
        ["decal.path"] = GroundAssetClass.DecalPath,
        ["decal.litter"] = GroundAssetClass.DecalLitter,
        ["decal.scorch"] = GroundAssetClass.DecalScorch,
        ["decal.stain"] = GroundAssetClass.DecalStain,
    };

    private static readonly Dictionary<string, GroundLayer> Layers = new(StringComparer.Ordinal)
    {
        ["decal"] = GroundLayer.Decal,
        ["cover"] = GroundLayer.Cover,
        ["accent"] = GroundLayer.Accent,
        ["landmark"] = GroundLayer.Landmark,
    };

    private static readonly Dictionary<string, GeometryMaterialId> Materials = new(StringComparer.Ordinal)
    {
        ["grass"] = GeometryMaterialId.Grass,
        ["dirt"] = GeometryMaterialId.Dirt,
        ["stone"] = GeometryMaterialId.Stone,
        ["sand"] = GeometryMaterialId.Sand,
        ["mud"] = GeometryMaterialId.Mud,
        ["snow"] = GeometryMaterialId.Snow,
        ["rock"] = GeometryMaterialId.Rock,
        ["grass_path"] = GeometryMaterialId.GrassPath,
    };

    private GroundAssetSet(List<GroundAsset> assets)
    {
        Assets = assets;
    }

    public IReadOnlyList<GroundAsset> Assets { get; }

    public static bool IsDecalClass(GroundAssetClass assetClass) => assetClass >= GroundAssetClass.DecalDirt;

    // docs: (file name for messages, manifest json). animationsGroundDir: where the manifests' "file" paths start.
    public static GroundAssetSet Parse(IEnumerable<(string Name, string Json)> docs, string animationsGroundDir, IRenderFaultSink? faults = null)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(animationsGroundDir);
        IRenderFaultSink sink = faults ?? RenderFaultLog.Shared;
        List<GroundAsset> assets = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach ((string name, string json) in docs)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    (!doc.RootElement.TryGetProperty("schema", out JsonElement schema) ||
                        schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema))
                {
                    Skip(sink, name, $"schema is not '{Schema}'");
                    continue;
                }

                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("assets", out JsonElement list) ||
                    list.ValueKind != JsonValueKind.Array)
                {
                    Skip(sink, name, "no assets array");
                    continue;
                }

                foreach (JsonElement entry in list.EnumerateArray())
                {
                    string? problem = TryReadEntry(entry, animationsGroundDir, out GroundAsset? asset);
                    if (asset == null)
                    {
                        Skip(sink, name, problem ?? "unreadable entry");
                    }
                    else if (!ids.Add(asset.Id))
                    {
                        Skip(sink, name, $"repeated id '{asset.Id}'");
                    }
                    else
                    {
                        assets.Add(asset);
                    }
                }
            }
            catch (JsonException exception)
            {
                Skip(sink, name, exception.Message);
            }
        }

        return assets.Count == 0 ? Empty : new GroundAssetSet(assets);
    }

    // A missing directory is the empty set.
    public static GroundAssetSet LoadDirectory(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        if (!Directory.Exists(dir))
        {
            return Empty;
        }

        List<(string Name, string Json)> docs = [];
        string[] files = [];
        try
        {
            files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RenderFaultLog.Shared.Report(FaultSource, exception);
        }

        foreach (string file in files)
        {
            try
            {
                docs.Add((Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RenderFaultLog.Shared.Report(FaultSource, exception);
            }
        }

        return Parse(docs, dir);
    }

    // The set the game ships: dream/animations/ground next to the executable.
    public static GroundAssetSet LoadShipped() => LoadDirectory(AssetPaths.Animation("ground"));

    private static void Skip(IRenderFaultSink sink, string doc, string reason) =>
        sink.Report(FaultSource, new InvalidDataException($"{doc}: skipped: {reason}"));

    private static string? TryReadEntry(JsonElement entry, string dir, out GroundAsset? asset)
    {
        asset = null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return "entry is not an object";
        }

        string? id = ReadString(entry, "id");
        string? file = ReadString(entry, "file");
        string? className = ReadString(entry, "class");
        string? layerName = ReadString(entry, "layer");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(file))
        {
            return "entry without id or file";
        }

        if (className == null || !Classes.TryGetValue(className, out GroundAssetClass assetClass))
        {
            return $"{id}: unknown class '{className}'";
        }

        if (layerName == null || !Layers.TryGetValue(layerName, out GroundLayer layer))
        {
            return $"{id}: unknown layer '{layerName}'";
        }

        if (IsDecalClass(assetClass) != (layer == GroundLayer.Decal))
        {
            return $"{id}: class '{className}' does not belong on layer '{layerName}'";
        }

        if (!TryReadNumber(entry, "radius", out float radius) || radius <= 0f ||
            !TryReadNumber(entry, "footprint", out float footprint) || footprint < 0f ||
            !TryReadNumber(entry, "height", out float height) || height < 0f ||
            !TryReadNumber(entry, "weight", out float weight) || weight <= 0f)
        {
            return $"{id}: radius, footprint, height or weight is missing or out of range";
        }

        float scaleMin = 1f;
        float scaleMax = 1f;
        if (entry.TryGetProperty("scale", out JsonElement scale))
        {
            if (scale.ValueKind != JsonValueKind.Array || scale.GetArrayLength() != 2 ||
                !scale[0].TryGetSingle(out scaleMin) || !scale[1].TryGetSingle(out scaleMax) ||
                !(scaleMin > 0f) || !(scaleMax >= scaleMin) || float.IsInfinity(scaleMax))
            {
                return $"{id}: bad scale";
            }
        }

        if (!entry.TryGetProperty("ground", out JsonElement groundList) || groundList.ValueKind != JsonValueKind.Array)
        {
            return $"{id}: no ground list";
        }

        List<GeometryMaterialId> ground = [];
        foreach (JsonElement item in groundList.EnumerateArray())
        {
            string? materialName = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (materialName == null || !Materials.TryGetValue(materialName, out GeometryMaterialId material))
            {
                return $"{id}: unknown ground material '{materialName}'";
            }

            if (!ground.Contains(material))
            {
                ground.Add(material);
            }
        }

        if (ground.Count == 0)
        {
            return $"{id}: empty ground list";
        }

        string modelPath = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
        asset = new GroundAsset(id, modelPath, assetClass, layer, radius, footprint, height, ground, weight, scaleMin, scaleMax);
        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryReadNumber(JsonElement element, string name, out float value)
    {
        value = 0f;
        return element.TryGetProperty(name, out JsonElement number) &&
            number.ValueKind == JsonValueKind.Number &&
            number.TryGetSingle(out value) &&
            float.IsFinite(value);
    }
}
