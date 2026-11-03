using Dream.Assets;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

// Terrain materials are resolved once into a fixed table. A static mesh stores the table index (its
// material slot) when it is uploaded, and the per-frame draw path only reads the table by slot, so
// no texture path string is built per mesh per frame.
public static class StaticTerrainMaterialCatalog
{
    // One slot per (material id, role) pair of the known material ids, plus one slot for unknown ids.
    public const int RoleCount = 4;
    public const int KnownMaterialCount = (int)GeometryMaterialId.Lamp + 1;
    public const int UnknownMaterialSlot = KnownMaterialCount * RoleCount;
    public const int MaterialSlotCount = UnknownMaterialSlot + 1;

    public static readonly Vector4 LampBaseColor = new(1f, 0.85f, 0.6f, 1f);
    public static readonly Vector3 LampEmissive = new(0.9f, 0.75f, 0.5f);

    private static readonly StaticTerrainMaterialRole[] GrassRoles =
        [StaticTerrainMaterialRole.Top, StaticTerrainMaterialRole.Side, StaticTerrainMaterialRole.Bottom];
    private static readonly StaticTerrainMaterialRole[] SingleRole = [StaticTerrainMaterialRole.Single];
    private static readonly StaticTerrainMaterial[] MaterialsBySlot = CreateMaterialTable();

    // Returns a shared, cached list; callers must not mutate it.
    public static IReadOnlyList<StaticTerrainMaterialRole> ResolveRoles(GeometryMaterialId materialId)
    {
        return materialId == GeometryMaterialId.Grass ? GrassRoles : SingleRole;
    }

    // Returns the cached material: its texture path is built once per slot, not per call.
    public static StaticTerrainMaterial Resolve(
        GeometryMaterialId materialId,
        StaticTerrainMaterialRole role = StaticTerrainMaterialRole.Single)
    {
        return MaterialsBySlot[ResolveSlot(materialId, role)];
    }

    // The small integer id a static mesh keeps for its material. Slots order by material id, then
    // role, so sorting meshes by slot groups them by material and role.
    public static int ResolveSlot(GeometryMaterialId materialId, StaticTerrainMaterialRole role)
    {
        int material = (int)materialId;
        int roleIndex = (int)role;
        if ((uint)material >= KnownMaterialCount || (uint)roleIndex >= RoleCount)
        {
            return UnknownMaterialSlot;
        }

        return (material * RoleCount) + roleIndex;
    }

    public static StaticTerrainMaterial GetBySlot(int slot)
    {
        return MaterialsBySlot[slot];
    }

    public static StaticTerrainMaterialRole ResolveRoleForNormal(
        GeometryMaterialId materialId,
        OpenTK.Mathematics.Vector3 normal)
    {
        if (materialId != GeometryMaterialId.Grass)
        {
            return StaticTerrainMaterialRole.Single;
        }

        if (normal.Y > 0.5f)
        {
            return StaticTerrainMaterialRole.Top;
        }

        if (normal.Y < -0.5f)
        {
            return StaticTerrainMaterialRole.Bottom;
        }

        return StaticTerrainMaterialRole.Side;
    }

    private static StaticTerrainMaterial[] CreateMaterialTable()
    {
        StaticTerrainMaterial[] table = new StaticTerrainMaterial[MaterialSlotCount];
        for (int material = 0; material < KnownMaterialCount; material++)
        {
            for (int role = 0; role < RoleCount; role++)
            {
                table[(material * RoleCount) + role] = Create((GeometryMaterialId)material, (StaticTerrainMaterialRole)role);
            }
        }

        // Unknown ids draw untextured magenta so they stand out.
        table[UnknownMaterialSlot] = new StaticTerrainMaterial(
            (GeometryMaterialId)(-1),
            StaticTerrainMaterialRole.Single,
            new Vector4(1f, 0f, 1f, 1f),
            string.Empty);
        return table;
    }

    private static StaticTerrainMaterial Create(GeometryMaterialId materialId, StaticTerrainMaterialRole role)
    {
        return materialId switch
        {
            GeometryMaterialId.Grass => new StaticTerrainMaterial(
                materialId,
                role,
                new Vector4(1f, 1f, 1f, 1f),
                ResolveGrassTexture(role)),
            GeometryMaterialId.Dirt => Single(materialId, ResolveForgeTexture("dirt")),
            GeometryMaterialId.Stone => Single(materialId, ResolveForgeTexture("stone")),
            GeometryMaterialId.Sand => Single(materialId, ResolveForgeTexture("sand")),
            GeometryMaterialId.Mud => Single(materialId, ResolveForgeTexture("mud")),
            GeometryMaterialId.Snow => Single(materialId, ResolveForgeTexture("snow")),
            GeometryMaterialId.Rock => Single(materialId, ResolveForgeTexture("rock")),
            GeometryMaterialId.GrassPath => Single(materialId, ResolveForgeTexture("grass_path")),
            // Untextured warm block that glows: the emissive term is added after exposure, so it
            // reads at night without lighting itself (its light on the surroundings comes from
            // LampLightIndex).
            GeometryMaterialId.Lamp => new StaticTerrainMaterial(
                materialId,
                StaticTerrainMaterialRole.Single,
                LampBaseColor,
                string.Empty)
            {
                Emissive = LampEmissive
            },
            _ => throw new ArgumentOutOfRangeException(nameof(materialId), materialId, "Not a known terrain material.")
        };
    }

    private static StaticTerrainMaterial Single(GeometryMaterialId materialId, string texturePath)
    {
        return new StaticTerrainMaterial(
            materialId,
            StaticTerrainMaterialRole.Single,
            new Vector4(1f, 1f, 1f, 1f),
            texturePath);
    }

    private static string ResolveGrassTexture(StaticTerrainMaterialRole role)
    {
        return role switch
        {
            StaticTerrainMaterialRole.Side => ResolveForgeTexture("grass_side"),
            StaticTerrainMaterialRole.Bottom => ResolveForgeTexture("grass_bottom"),
            _ => ResolveForgeTexture("grass_top")
        };
    }

    // Baked surface tiles from tools/ui-assets/gen_terrain.py, copied to library/forge/<name>/<name>.albedo.png.
    private static string ResolveForgeTexture(string name)
    {
        return Path.Combine(
            AssetPaths.AnimationsDirectory,
            "library",
            "forge",
            name,
            name + ".albedo.png");
    }
}
