using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Rendering.World;

// GL is unavailable in tests, so the corner weight function of the terrain program is mirrored here
// (kept textually close to dreamTerrainCornerWeights / the weight setup of dreamTerrainAlbedo in
// cubeFragment.glsl) and checked for continuity across cell edges, plus pins on the shader source.
public class TerrainBlendCornerWeightTests
{
    private const float Band = 0.35f;

    // ---- the mirror -------------------------------------------------------------------------

    private static (float X, float Y, float Z) SplitMissing(float m, float wp, bool okp, float wq, bool okq)
    {
        float sp = okp ? wp : 0f;
        float sq = okq ? wq : 0f;
        float t = sp + sq;
        if (t > 1e-6f)
        {
            return (m * sp / t, m * sq / t, 0f);
        }

        return (0f, 0f, m);
    }

    private static float[] CornerWeights(float ax, float ay, bool px, bool pz, bool pd)
    {
        float r0 = (1f - ax) * (1f - ay);
        float r1 = ax * (1f - ay);
        float r2 = (1f - ax) * ay;
        float r3 = ax * ay;
        float w0 = r0;
        float w1 = px ? r1 : 0f;
        float w2 = pz ? r2 : 0f;
        float w3 = pd ? r3 : 0f;
        if (!px)
        {
            var g = SplitMissing(r1, r0, true, r3, pd);
            w0 += g.X;
            w3 += g.Y;
            w2 += g.Z;
        }

        if (!pz)
        {
            var g = SplitMissing(r2, r0, true, r3, pd);
            w0 += g.X;
            w3 += g.Y;
            w1 += g.Z;
        }

        if (!pd)
        {
            var g = SplitMissing(r3, r2, pz, r1, px);
            w2 += g.X;
            w1 += g.Y;
            w0 += g.Z;
        }

        return [w0, w1, w2, w3];
    }

    // The per-cell weights at local position (lx, lz) inside cell (cx, cz). A cell is present when its
    // value in `cells` is non-zero (the value is then the id used as its "material").
    private static Dictionary<int, float> Evaluate(int[,] cells, int cx, int cz, float lx, float lz)
    {
        lx = Math.Clamp(lx, 0f, 1f);
        lz = Math.Clamp(lz, 0f, 1f);
        float ex = Math.Min(lx, 1f - lx);
        float ey = Math.Min(lz, 1f - lz);
        float ax = 0.5f * (1f - Math.Clamp(ex / Band, 0f, 1f));
        float ay = 0.5f * (1f - Math.Clamp(ey / Band, 0f, 1f));
        int dx = lx >= 0.5f ? 1 : -1;
        int dz = lz >= 0.5f ? 1 : -1;
        int own = cells[cx, cz];
        int tx = ax > 0f ? cells[cx + dx, cz] : 0;
        int tz = ay > 0f ? cells[cx, cz + dz] : 0;
        int td = ax > 0f && ay > 0f ? cells[cx + dx, cz + dz] : 0;
        float[] w = CornerWeights(ax, ay, tx != 0, tz != 0, td != 0);
        int[] id = [own, tx, tz, td];
        Dictionary<int, float> result = new();
        for (int s = 0; s < 4; s++)
        {
            if (w[s] > 0f)
            {
                result[id[s]] = result.GetValueOrDefault(id[s]) + w[s];
            }
        }

        return result;
    }

    // ---- continuity -------------------------------------------------------------------------

    private static void AssertSame(Dictionary<int, float> a, Dictionary<int, float> b, string context)
    {
        foreach (int key in a.Keys.Union(b.Keys))
        {
            Assert.True(
                MathF.Abs(a.GetValueOrDefault(key) - b.GetValueOrDefault(key)) < 1e-5f,
                $"{context}: material {key} weighs {a.GetValueOrDefault(key)} vs {b.GetValueOrDefault(key)}");
        }
    }

    [Fact]
    public void Weights_Are_The_Same_From_Both_Cells_Sharing_An_Edge_For_Every_Presence_Pattern()
    {
        int checkedEdges = 0;
        // Every presence pattern of a 3x3 block, every edge between two present cells inside it.
        for (int mask = 0; mask < 512; mask++)
        {
            int[,] cells = new int[5, 5];
            for (int i = 0; i < 9; i++)
            {
                cells[1 + (i % 3), 1 + (i / 3)] = (mask & (1 << i)) != 0 ? i + 1 : 0;
            }

            for (int cz = 1; cz <= 3; cz++)
            {
                for (int cx = 1; cx <= 3; cx++)
                {
                    if (cells[cx, cz] == 0)
                    {
                        continue;
                    }

                    // Edge to the +x neighbour and to the +z neighbour.
                    if (cx < 3 && cells[cx + 1, cz] != 0)
                    {
                        for (int k = 0; k <= 10; k++)
                        {
                            float t = k / 10f;
                            AssertSame(
                                Evaluate(cells, cx, cz, 1f, t),
                                Evaluate(cells, cx + 1, cz, 0f, t),
                                $"mask {mask} x-edge of ({cx},{cz}) t={t}");
                        }

                        checkedEdges++;
                    }

                    if (cz < 3 && cells[cx, cz + 1] != 0)
                    {
                        for (int k = 0; k <= 10; k++)
                        {
                            float t = k / 10f;
                            AssertSame(
                                Evaluate(cells, cx, cz, t, 1f),
                                Evaluate(cells, cx, cz + 1, t, 0f),
                                $"mask {mask} z-edge of ({cx},{cz}) t={t}");
                        }

                        checkedEdges++;
                    }
                }
            }
        }

        Assert.True(checkedEdges > 1000);
    }

    [Fact]
    public void The_Reported_Case_Has_No_Step_Along_The_Shared_Edge()
    {
        // A(1,1) and B(1,2) exposed, Xa(2,1) missing, Dg(2,2) exposed.
        int[,] cells = new int[4, 4];
        cells[1, 1] = 1;
        cells[1, 2] = 2;
        cells[2, 2] = 4;
        for (float t = 0.6f; t <= 1f; t += 0.01f)
        {
            // The A|B edge is z = 2: from A at local z 1, from B at local z 0, x near the x = 2 corner.
            AssertSame(Evaluate(cells, 1, 1, t, 1f), Evaluate(cells, 1, 2, t, 0f), $"x={t}");
        }
    }

    [Fact]
    public void Weights_Are_Own_Only_At_And_Beyond_The_Band_Boundary()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            bool px = (mask & 1) != 0, pz = (mask & 2) != 0, pd = (mask & 4) != 0;
            float[] w = CornerWeights(0f, 0f, px, pz, pd);
            Assert.Equal([1f, 0f, 0f, 0f], w);
        }

        // Only the X band active, the Z neighbour missing: nothing leaks to the diagonal.
        float[] edge = CornerWeights(0f, 0.3f, true, false, true);
        Assert.Equal(0f, edge[1]);
        Assert.Equal(0f, edge[3]);
        Assert.Equal(1f, edge[0], 5);
    }

    [Fact]
    public void Weights_Are_Continuous_Across_The_Band_Boundary_And_Sum_To_One()
    {
        for (int mask = 0; mask < 512; mask++)
        {
            int[,] cells = new int[5, 5];
            for (int i = 0; i < 9; i++)
            {
                cells[1 + (i % 3), 1 + (i / 3)] = (mask & (1 << i)) != 0 ? i + 1 : 0;
            }

            if (cells[2, 2] == 0)
            {
                continue;
            }

            foreach (float lz in new[] { 0.02f, 0.2f, 0.5f, 0.8f, 0.98f })
            {
                for (float lx = 0.001f; lx < 0.995f; lx += 0.004f)
                {
                    var w0 = Evaluate(cells, 2, 2, lx, lz);
                    var w1 = Evaluate(cells, 2, 2, lx + 0.004f, lz);
                    Assert.True(MathF.Abs(w0.Values.Sum() - 1f) < 1e-4f);
                    // A 4 mm step moves a weight by at most the interpolation slope times 4 mm; the
                    // cell-half switch at 0.5 is outside the band so it is continuous too.
                    foreach (int key in w0.Keys.Union(w1.Keys))
                    {
                        Assert.True(
                            MathF.Abs(w0.GetValueOrDefault(key) - w1.GetValueOrDefault(key)) < 0.03f,
                            $"mask {mask} lz={lz} lx={lx} material {key}");
                    }
                }
            }
        }
    }

    // ---- shader pins ------------------------------------------------------------------------

    [Fact]
    public void The_Shader_Takes_The_Cell_From_The_Corner_And_Clamps_Uv_Without_A_Neighbour_Lookup()
    {
        string shader = StripComments(ReadShader());
        string albedo = Body(shader, "vec3 dreamTerrainAlbedo(");

        Assert.DoesNotContain("floor(uv)", albedo);
        Assert.Contains("vec2 l = clamp(uv, 0.0, 1.0);", albedo);
        Assert.Contains("dreamTerrainCornerWeights(a, px, pz, pd)", albedo);
    }

    [Fact]
    public void The_Shader_Corner_Weight_Functions_Keep_The_Statements_The_Mirror_Copies()
    {
        string shader = StripComments(ReadShader());
        string split = Squash(Body(shader, "vec3 dreamTerrainSplitMissing("));
        string[] splitStatements =
        [
            "floatsp=okp?wp:0.0;",
            "floatsq=okq?wq:0.0;",
            "floatt=sp+sq;",
            "if(t>1e-6){returnm*vec3(sp,sq,0.0)/t;}",
            "returnvec3(0.0,0.0,m);"
        ];
        AssertInOrder(split, splitStatements);

        string weights = Squash(Body(shader, "vec4 dreamTerrainCornerWeights("));
        string[] weightStatements =
        [
            "floatr0=(1.0-a.x)*(1.0-a.y);",
            "floatr1=a.x*(1.0-a.y);",
            "floatr2=(1.0-a.x)*a.y;",
            "floatr3=a.x*a.y;",
            "floatw0=r0;",
            "floatw1=px?r1:0.0;",
            "floatw2=pz?r2:0.0;",
            "floatw3=pd?r3:0.0;",
            "if(!px){vec3g=dreamTerrainSplitMissing(r1,r0,true,r3,pd);w0+=g.x;w3+=g.y;w2+=g.z;}",
            "if(!pz){vec3g=dreamTerrainSplitMissing(r2,r0,true,r3,pd);w0+=g.x;w3+=g.y;w1+=g.z;}",
            "if(!pd){vec3g=dreamTerrainSplitMissing(r3,r2,pz,r1,px);w2+=g.x;w1+=g.y;w0+=g.z;}",
            "returnvec4(w0,w1,w2,w3);"
        ];
        AssertInOrder(weights, weightStatements);

        // The band setup the mirror's Evaluate copies.
        string albedo = Squash(Body(shader, "vec3 dreamTerrainAlbedo("));
        Assert.Contains("vec2e=min(l,1.0-l);", albedo);
        Assert.Contains("vec2a=0.5*(1.0-clamp(e/max(uTerrainBlendBand,0.001),0.0,1.0));", albedo);
        Assert.Contains("ivec2dir=ivec2(l.x>=0.5?1:-1,l.y>=0.5?1:-1);", albedo);
        Assert.Contains("inttx=a.x>0.0?dreamTerrainTexel(cell+ivec3(dir.x,0,0)):0;", albedo);
        Assert.Contains("inttz=a.y>0.0?dreamTerrainTexel(cell+ivec3(0,0,dir.y)):0;", albedo);
        Assert.Contains("inttd=(a.x>0.0&&a.y>0.0)?dreamTerrainTexel(cell+ivec3(dir.x,0,dir.y)):0;", albedo);
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", string.Empty);

    private static void AssertInOrder(string squashedBody, string[] statements)
    {
        int at = 0;
        foreach (string statement in statements)
        {
            int found = squashedBody.IndexOf(statement, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"shader no longer has (in order): {statement}");
            at = found + statement.Length;
        }
    }

    [Fact]
    public void Blendable_Materials_Have_A_White_Base_Color_Factor_So_Neighbour_Tiles_Are_Not_Tinted()
    {
        for (int id = 0; id < TerrainMaterialMap.BlendableMaterialCount; id++)
        {
            foreach (StaticTerrainMaterialRole role in StaticTerrainMaterialCatalog.ResolveRoles((GeometryMaterialId)id))
            {
                Assert.Equal(OpenTK.Mathematics.Vector4.One, StaticTerrainMaterialCatalog.Resolve((GeometryMaterialId)id, role).BaseColor);
            }
        }
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source.Substring(open, i - open + 1);
            }
        }

        throw new InvalidOperationException(signature);
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string ReadShader() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", "cubeFragment.glsl"));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
