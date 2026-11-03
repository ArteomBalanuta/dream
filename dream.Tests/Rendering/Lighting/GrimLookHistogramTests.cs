using Dream.Client.Presentation;
using Dream.Rendering.Lighting;
using Dream.Rendering.Sky;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Lighting;

// WS-B, the fast companion of tools/ui-assets/tests/check_look.py (A1, the bimodal value structure).
// It runs the cube shader on the CPU (the mirror in WaveT1DayCycleLightingTests: Ambient, Direct, fog,
// and pointLights.glsl's PointLight) over a ground grid plus vertical faces, with the braziers that
// PlacedLightIndex places on the generated 56 x 56 startup world, at the night and dusk keyframes,
// from the spawn-corner views the capture harness produces, and checks the same bands over the world
// pixels (the HUD band is excluded, as check_look.py --hud-rows does).
//
// Differences from a captured frame: mean-colour albedo instead of texels (so no texture grain) and no
// actors, flames or FX. Near-black, mean and saturation are check_look.py's constants and move with it. The bright-share
// floor is DELIBERATELY lower than check_look.py's 0.020 - see ModelBrightShareMin below for why:
// this model has no particle FX, and check_look.py on a real capture still owns the 0.020 gate. Each column's
// surface is the material the world generator gives it, at the baked tiles' measured mean albedo (see
// MaterialAlbedo).
public class GrimLookHistogramTests(ITestOutputHelper output)
{
    // check_look.py: BLACK_V / BLACK_SHARE_MIN / BRIGHT_V / BRIGHT_SHARE_MIN / MEAN_V_MAX / MEAN_SAT_MAX.
    private const float BlackV = 0.10f;
    private const float BlackShareMin = 0.45f;
    private const float BrightV = 0.60f;
    private const float BrightShareMin = 0.020f;
    private const float ModelBrightShareMin = 0.002f; // lights only, no FX halo; see the assertion
    private const float MeanVMax = 0.18f;
    private const float MeanSatMax = 0.38f;

    // Mean RGB of each material's baked terrain tile (tools/ui-assets/terrain/*.png, the forge tiles
    // StaticTerrainMaterialCatalog now points at; measured with PIL; colour space is sampled as linear).
    private static Vector3 MaterialAlbedo(GeometryMaterialId material) => material switch
    {
        GeometryMaterialId.Grass => new Vector3(0.262f, 0.295f, 0.230f),
        GeometryMaterialId.Dirt => new Vector3(0.307f, 0.268f, 0.233f),
        GeometryMaterialId.Stone => new Vector3(0.292f, 0.292f, 0.286f),
        GeometryMaterialId.Sand => new Vector3(0.318f, 0.293f, 0.262f),
        GeometryMaterialId.Mud => new Vector3(0.273f, 0.245f, 0.217f),
        GeometryMaterialId.Snow => new Vector3(0.266f, 0.280f, 0.295f),
        GeometryMaterialId.Rock => new Vector3(0.284f, 0.276f, 0.264f),
        GeometryMaterialId.GrassPath => new Vector3(0.312f, 0.317f, 0.259f),
        _ => new Vector3(0.307f, 0.268f, 0.233f),
    };

    // Where the capture harness's player stood in the captures (x, z): the spawn corner of the 56 x 56 world.
    private static readonly Vector2[] SpawnViews = [new(5.5f, 8.5f), new(5f, 13f), new(4.5f, 15.4f)];

    private const float PlayerEyeY = 1.5f;

    // The harness window is 1440 x 1024; the world region is the top 884 rows (the HUD band is 140).
    // Every PixelStep-th pixel is traced.
    private const int WindowWidth = 1440;
    private const int WindowHeight = 1024;
    private const int WorldRows = 884;
    private const int PixelStep = 16;
    private const float Aspect = (float)WindowWidth / WindowHeight;

    [Theory]
    [InlineData(0f, "midnight")]
    [InlineData(20f, "20:00 night")]
    [InlineData(19f, "19:00 dusk")]
    [InlineData(5f, "05:00 blue hour")]
    public void NightAndDusk_ReachTheBimodalBands_OnTheGroundAroundABrazier(float hour, string name)
    {
        PlacedLightIndex index = new();
        LightCollector collector = new();
        // The game's own startup world: the default generator at the integrated server's 56 x 56 size.
        MapDefinition definition = new(56, 8, 56, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        Map map = new(new GeneratedMapSource(definition), physics: null, headless: false);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(28f, 2f, 28f), 200f);
        index.Collect(export, new Vector3(28f, 12f, 28f), collector);
        Assert.NotEmpty(index.Placements);

        // The top cell of every column: its top face height and material.
        Dictionary<(int, int), (int Top, GeometryMaterialId Material)> columns = [];
        foreach (Cube cube in export.CompatibilityCubes)
        {
            (int, int) key = ((int)MathF.Floor(cube.Position.X), (int)MathF.Floor(cube.Position.Z));
            int top = (int)MathF.Floor(cube.Position.Y) + 1;
            if (!columns.TryGetValue(key, out var best) || top > best.Top)
            {
                columns[key] = (top, cube.MaterialId);
            }
        }

        FrameLighting sky = SkyModel.Evaluate(hour / 24f).Lighting;

        // The frames the harness captures: the player at the spawn corner, seen through the real
        // isometric camera, with the map edge in view. A ray per pixel marches over the generated
        // columns, so the pixel density, the fog distance and the share of void are the frame's own.
        // The void (map edge, sky) is cleared to a quarter of the horizon colour in the isometric
        // pass (PlayScene.Render, clearScale), so it counts as pixels too.
        Vector3 voidColour = Quantize(sky.FogColor.Xyz * 0.25f);
        Tally tally = new();
        foreach (Vector2 player in SpawnViews)
        {
            Vector3 focus = new(player.X, PlayerEyeY, player.Y);
            Vector3 camera = focus + IsometricView.FocusToCamera;
            FrameLighting world = FrameLightingBuffer.ForWorld(in sky, camera);
            PointLight[] lights = SelectLights(index.Placements, camera);
            Matrix4 view = Matrix4.LookAt(camera, focus, Vector3.UnitY);
            Matrix4 inverseView = view.Inverted();
            float tanHalfFov = MathF.Tan(MathHelper.DegreesToRadians(IsometricView.FieldOfViewDegrees) / 2f);

            for (int py = 0; py < WorldRows; py += PixelStep)
            {
                for (int px = 0; px < WindowWidth; px += PixelStep)
                {
                    float ndcX = (px + 0.5f) / WindowWidth * 2f - 1f;
                    float ndcY = 1f - (py + 0.5f) / WindowHeight * 2f;
                    Vector3 rayView = new(ndcX * tanHalfFov * Aspect, ndcY * tanHalfFov, -1f);
                    Vector3 direction = Vector3.Normalize(Vector3.TransformVector(rayView, inverseView));
                    tally.Add(Trace(in world, lights, columns, camera, direction, voidColour));
                }
            }
        }

        output.WriteLine($"{name}: near-black {tally.BlackShare:F3} (>= {BlackShareMin}), bright {tally.BrightShare:F3} (>= {BrightShareMin}), mean v {tally.MeanV:F3} (<= {MeanVMax}), mean sat {tally.MeanSat:F3} (<= {MeanSatMax})");

        Assert.True(tally.BlackShare >= BlackShareMin, $"{name}: near-black share {tally.BlackShare}");
        // The 0.020 bright share is met by the captured frame only with the lamp-class fire halo
        // (FxSimulation) on top of the light: with the brazier intensity held low enough that the pool does
        // not clip to white, the lights alone give a bright tail of about 0.004. The model has no FX, so it
        // asserts the tail exists and is not the no-brazier tail; check_look.py on a capture owns 0.020.
        Assert.True(tally.BrightShare >= ModelBrightShareMin, $"{name}: bright share {tally.BrightShare}");
        Assert.True(tally.MeanV <= MeanVMax, $"{name}: mean value {tally.MeanV}");
        Assert.True(tally.MeanSat <= MeanSatMax, $"{name}: mean saturation {tally.MeanSat}");
    }

    // The same night without braziers: darkness alone fails the bright tail, which is what the gate
    // exists to enforce (a grade cannot fake it).
    [Fact]
    public void WithoutBraziers_TheNightIsDarkButHasNoBrightTail()
    {
        FrameLighting sky = SkyModel.Evaluate(0f).Lighting;
        FrameLighting world = FrameLightingBuffer.ForWorld(in sky, new Vector3(28f, 14f, 40f));
        Tally tally = new();
        for (float x = 16f; x < 40f; x += 0.5f)
        {
            for (float z = 16f; z < 40f; z += 0.5f)
            {
                tally.Add(Shade(in world, [], Vector3.UnitY, new Vector3(x, 1f, z), MaterialAlbedo(GeometryMaterialId.Dirt)));
            }
        }

        Assert.True(tally.BlackShare >= BlackShareMin);
        Assert.True(tally.BrightShare < ModelBrightShareMin);
    }

    private sealed class Tally
    {
        private int _pixels;
        private int _black;
        private int _bright;
        private double _sumV;
        private double _sumSat;

        public float BlackShare => _black / (float)_pixels;

        public float BrightShare => _bright / (float)_pixels;

        public float MeanV => (float)(_sumV / _pixels);

        public float MeanSat => (float)(_sumSat / _pixels);

        public void Add(Vector3 c)
        {
            float v = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
            float mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
            _pixels++;
            _sumV += v;
            _sumSat += v > 0f ? (v - mn) / v : 0f;
            if (v < BlackV)
            {
                _black++;
            }

            if (v > BrightV)
            {
                _bright++;
            }
        }
    }

    // What the renderer's LightSelection would keep, nearest first, capped at LightList.MaxLights.
    private static PointLight[] SelectLights(IReadOnlyList<PlacedLight> placements, Vector3 camera)
    {
        List<PointLight> lights = [];
        foreach (PlacedLight p in placements)
        {
            if (Vector3.Distance(p.Position, camera) <= PlacedLightIndex.CollectDistance)
            {
                lights.Add(new PointLight(p.Position, p.Radius, p.Color));
            }
        }

        return [.. lights.OrderBy(l => Vector3.DistanceSquared(l.Position, camera)).Take(LightList.MaxLights)];
    }

    // Marches the ray over the columns' heightfield until it enters a column's solid part; the face
    // it entered through gives the normal. A ray that leaves the map or the render distance is void.
    private static Vector3 Trace(
        in FrameLighting world, PointLight[] lights,
        Dictionary<(int, int), (int Top, GeometryMaterialId Material)> columns,
        Vector3 origin, Vector3 direction, Vector3 voidColour)
    {
        const float Step = 0.05f;
        Vector3 previous = origin;
        (int, int) previousKey = ((int)MathF.Floor(origin.X), (int)MathF.Floor(origin.Z));
        for (float t = 0f; t < WorldVisibility.MaxRenderDistance; t += Step)
        {
            Vector3 p = origin + direction * t;
            (int, int) key = ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z));
            if (columns.TryGetValue(key, out var column) && p.Y <= column.Top)
            {
                Vector3 normal = Vector3.UnitY;
                if (p.Y < column.Top - 0.1f)
                {
                    normal = key.Item1 != previousKey.Item1
                        ? new Vector3(-MathF.Sign(direction.X), 0f, 0f)
                        : new Vector3(0f, 0f, -MathF.Sign(direction.Z));
                }

                return Shade(in world, lights, normal, p, MaterialAlbedo(column.Material));
            }

            if (p.Y < 0f)
            {
                break;
            }

            previous = p;
            previousKey = key;
        }

        _ = previous;
        return voidColour;
    }

    private static Vector3 Shade(in FrameLighting world, PointLight[] lights, Vector3 normal, Vector3 fragPos, Vector3 albedo)
    {
        Vector3 lit = (Ambient(in world, normal) + Direct(in world, normal) + PointLights(lights, normal, fragPos)) * albedo * world.Params.X;
        return Quantize(ApplyFog(in world, lit, fragPos));
    }

    // The 8-bit back buffer: saturate and round to 1/255, as the frame is captured.
    private static Vector3 Quantize(Vector3 c) => new(
        MathF.Round(Math.Clamp(c.X, 0f, 1f) * 255f) / 255f,
        MathF.Round(Math.Clamp(c.Y, 0f, 1f) * 255f) / 255f,
        MathF.Round(Math.Clamp(c.Z, 0f, 1f) * 255f) / 255f);

    // CPU mirrors, as in WaveT1DayCycleLightingTests (cubeFragment.glsl over lighting.glsl and
    // pointLights.glsl).
    private static Vector3 PointLights(PointLight[] lights, Vector3 n, Vector3 fragPos)
    {
        Vector3 sum = Vector3.Zero;
        foreach (PointLight light in lights)
        {
            Vector3 toLight = light.Position - fragPos;
            float d2 = Vector3.Dot(toLight, toLight);
            float s = d2 / (light.Radius * light.Radius);
            float f = Math.Clamp(1f - s, 0f, 1f);
            f = f * f / (1f + 1.5f * s);
            float lambert = MathF.Max(Vector3.Dot(n, toLight / MathF.Sqrt(MathF.Max(d2, 1e-4f))), 0f);
            sum += light.Color * (f * (0.25f + 0.75f * lambert));
        }

        return sum;
    }

    private static Vector3 Ambient(in FrameLighting l, Vector3 n) =>
        Vector3.Lerp(l.AmbientGround.Xyz, l.AmbientSky.Xyz, n.Y * 0.5f + 0.5f);

    private static Vector3 Direct(in FrameLighting l, Vector3 n) =>
        MathF.Max(Vector3.Dot(n, l.SunDirection.Xyz), 0f) * l.SunDirection.W * l.SunColor.Xyz
        + MathF.Max(Vector3.Dot(n, l.MoonDirection.Xyz), 0f) * l.MoonDirection.W * l.MoonColor.Xyz;

    private static Vector3 ApplyFog(in FrameLighting l, Vector3 c, Vector3 fragPos)
    {
        float d = MathF.Max(Vector3.Distance(fragPos, l.CameraPosition.Xyz) - l.CameraPosition.W, 0f);
        float k = l.FogColor.W * d;
        return Vector3.Lerp(l.FogColor.Xyz, c, Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f));
    }
}
