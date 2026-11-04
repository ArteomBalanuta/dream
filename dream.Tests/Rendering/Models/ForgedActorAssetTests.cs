using System.IO;
using System.Linq;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;

// The forged actors of the models-and-rigs wave: the skinned hound (M4) and the three rigid
// villagers (M5). Every assertion here is against the SHIPPED GLB, the way
// StaticTerrainMaterialCatalogTests asserts its textures exist, so a wave that wires a path without
// forging the asset fails loudly instead of drawing nothing.
public class ForgedActorAssetTests
{
    public static TheoryData<string> NpcModelPaths => new()
    {
        NpcAssetDefinition.VendorModelPath,
        NpcAssetDefinition.SmithModelPath,
        NpcAssetDefinition.WardenModelPath
    };

    [Fact]
    public void HoundModelPath_Resolves_Under_Runtime_Enemy_Animation_Directory()
    {
        Assert.EndsWith(
            Path.Combine("animations", "enemies", "hound.glb"),
            BeastAssetDefinition.HoundModelPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHound_IsShipped()
    {
        Assert.True(
            File.Exists(BeastAssetDefinition.HoundModelPath),
            $"Missing forged beast at {BeastAssetDefinition.HoundModelPath}");
    }

    [Theory]
    [MemberData(nameof(NpcModelPaths))]
    public void TheVillagers_AreShipped(string modelPath)
    {
        Assert.EndsWith(Path.Combine("animations", "npcs"), Path.GetDirectoryName(modelPath)!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(modelPath), $"Missing forged villager at {modelPath}");
    }

    // C3: one skin, 32 joints at most (the engine's limit is 64 and above it LoadSkinning silently
    // returns null and the mesh draws in bind pose), every primitive skinned, weights summing to 1.
    [Fact]
    public void TheHound_IsSkinned_WithinTheJointBudget()
    {
        GltfSkinningAnalysisResult analysis = GltfSkinningAnalysis.Analyze(BeastAssetDefinition.HoundModelPath);

        Assert.True(analysis.HasSkins);
        Assert.True(analysis.HasSkinnedPrimitives);
        Assert.InRange(analysis.JointCount, 1, 32);
        Assert.True(analysis.JointCount <= AnimatedModelData.MaxSupportedJoints);
    }

    [Fact]
    public void TheHound_SkinsEveryPrimitive_WithWeightsThatSumToOne()
    {
        ModelRoot model = ModelRoot.Load(BeastAssetDefinition.HoundModelPath);

        Assert.Single(model.LogicalSkins);
        foreach (MeshPrimitive primitive in model.LogicalMeshes.SelectMany(mesh => mesh.Primitives))
        {
            Assert.NotNull(primitive.GetVertexAccessor("JOINTS_0"));
            Assert.NotNull(primitive.GetVertexAccessor("WEIGHTS_0"));
            IReadOnlyList<System.Numerics.Vector4> weights = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array();
            for (int i = 0; i < weights.Count; i++)
            {
                System.Numerics.Vector4 w = weights[i];
                Assert.Equal(1f, w.X + w.Y + w.Z + w.W, 3);
            }
        }
    }

    [Fact]
    public void TheHound_Loads_WithJoints_AndItsClips()
    {
        AnimatedModelData data = AnimatedModelData.Load(BeastAssetDefinition.HoundModelPath);

        Assert.NotNull(data.SkinJoints);
        Assert.True(data.Skeleton.HasSkin, "The skin exceeded AnimatedModelData.MaxSupportedJoints, so it draws in bind pose.");
        Assert.True(data.Skeleton.TryGetClip(BeastAssetDefinition.IdleClip, out _));
        Assert.True(data.Skeleton.TryGetClip(BeastAssetDefinition.MoveClip, out _));
        Assert.True(data.Skeleton.TryGetClip(BeastAssetDefinition.AttackClip, out _));
        Assert.True(data.Skeleton.TryGetClip(BeastAssetDefinition.DieClip, out _));
    }

    // M4: the deformation is real, not a bind-pose draw. A joint matrix away from the bind pose is
    // what the vertex shader multiplies the skinned vertex by, so a moved joint matrix times a real
    // weighted vertex is a moved vertex.
    [Fact]
    public void TheHound_DeformsAwayFromTheBindPose_AndItsVerticesMove()
    {
        AnimatedModelData data = AnimatedModelData.Load(BeastAssetDefinition.HoundModelPath);
        ModelRoot model = data.Model;
        Node meshNode = model.LogicalNodes.First(node => node.Mesh != null && node.Skin != null);
        Matrix4 meshWorld = meshNode.WorldMatrix.ToOpenTK();

        AnimatedModelInstance instance = new(data.Skeleton);
        Matrix4[] bind = instance.ComputeJointMatrices(meshWorld).ToArray();

        instance.PlayLoop(BeastAssetDefinition.MoveClip);
        instance.Update(0.25f);
        Assert.True(instance.TimeSeconds > 0f);
        Matrix4[] posed = instance.ComputeJointMatrices(meshWorld).ToArray();

        float maxJointShift = 0f;
        for (int i = 0; i < bind.Length; i++)
        {
            maxJointShift = MathF.Max(maxJointShift, (posed[i].Row3.Xyz - bind[i].Row3.Xyz).Length);
        }

        Assert.True(maxJointShift > 1e-3f, $"No joint moved at t>0 (max shift {maxJointShift}); the mesh would draw in bind pose.");

        MeshPrimitive primitive = meshNode.Mesh!.Primitives[0];
        IReadOnlyList<System.Numerics.Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        IReadOnlyList<System.Numerics.Vector4> joints = primitive.GetVertexAccessor("JOINTS_0").AsVector4Array();
        IReadOnlyList<System.Numerics.Vector4> weights = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array();

        float maxVertexShift = 0f;
        for (int i = 0; i < positions.Count; i++)
        {
            Vector4 position = new(positions[i].X, positions[i].Y, positions[i].Z, 1f);
            Vector3 atBind = Vector4.TransformRow(position, Weighted(bind, joints[i], weights[i])).Xyz;
            Vector3 atPose = Vector4.TransformRow(position, Weighted(posed, joints[i], weights[i])).Xyz;
            maxVertexShift = MathF.Max(maxVertexShift, (atPose - atBind).Length);
        }

        Assert.True(maxVertexShift > 1e-3f, $"No vertex moved at t>0 (max shift {maxVertexShift}).");
    }

    // C1 / F2: the villagers are rigid part hierarchies with the shared node names, so the pose
    // layers and the held-item lookups keep working for anything posed by node name. A skin here
    // would silently cost every node override.
    [Theory]
    [MemberData(nameof(NpcModelPaths))]
    public void TheVillagers_AreRigid_WithTheSharedNodeNames_AndTwoClips(string modelPath)
    {
        AnimatedModelData data = AnimatedModelData.Load(modelPath);

        Assert.Null(data.SkinJoints);
        Assert.Empty(data.Model.LogicalSkins);
        Assert.True(data.Skeleton.TryGetClip(NpcAssetDefinition.IdleClip, out _));
        Assert.True(data.Skeleton.TryGetClip(NpcAssetDefinition.WalkClip, out _));

        HashSet<string> renderable = new(StringComparer.Ordinal);
        for (int i = 0; i < data.Skeleton.RenderableNodeCount; i++)
        {
            renderable.Add(data.Skeleton.GetRenderableNode(i).Name);
        }

        foreach (string name in new[] { "torso", "head", "arm-left", "arm-right", "leg-left", "leg-right" })
        {
            Assert.Contains(name, renderable);
        }
    }

    // Every clip name PlayerAnimationStateMachine can return must resolve on the SHIPPED player,
    // the three new directional ones included. Only check_char.py's REQUIRED_CLIPS guarded them,
    // and `dotnet test` does not run Python - so a re-forge that dropped `walk-strafe-right` would
    // ship green, PlayerAnimationStateMachine.FallbackClip would silently substitute the plain walk
    // cycle, and the only symptom would be the moonwalk this wave fixed coming back.
    public static TheoryData<string> PlayerClipNames => new()
    {
        PlayerAnimationStateMachine.IdleClip,
        PlayerAnimationStateMachine.WalkClip,
        PlayerAnimationStateMachine.SprintClip,
        PlayerAnimationStateMachine.DieClip,
        PlayerAnimationStateMachine.MeleeClip,
        PlayerAnimationStateMachine.ShootClip,
        PlayerAnimationStateMachine.WalkBackClip,
        PlayerAnimationStateMachine.StrafeLeftClip,
        PlayerAnimationStateMachine.StrafeRightClip
    };

    [Theory]
    [MemberData(nameof(PlayerClipNames))]
    public void TheShippedPlayer_CarriesEveryClipTheStateMachinePlays(string clipName)
    {
        AnimatedModelData data = AnimatedModelData.Load(PlayerModelAssetDefinition.ModelPath);

        Assert.True(
            data.Skeleton.TryGetClip(clipName, out _),
            $"The shipped player GLB has no '{clipName}' clip; the state machine's fallback would hide it.");
    }

    // And the directional three are really distinct clips, not three names on one animation: the
    // fallback maps them onto `walk`, so a generator that aliased them would look like a pass above.
    [Fact]
    public void TheDirectionalClips_AreTheirOwnAnimations_NotAliasesOfTheWalkCycle()
    {
        AnimatedModelData data = AnimatedModelData.Load(PlayerModelAssetDefinition.ModelPath);
        Node leg = data.Model.LogicalNodes.First(n => string.Equals(n.Name, "leg-left", StringComparison.Ordinal));
        string[] directional =
        [
            PlayerAnimationStateMachine.WalkBackClip,
            PlayerAnimationStateMachine.StrafeLeftClip,
            PlayerAnimationStateMachine.StrafeRightClip
        ];

        Assert.True(data.Skeleton.TryGetClip(PlayerAnimationStateMachine.WalkClip, out Animation? walk));
        System.Numerics.Quaternion[] walkKeys = LegRotationKeys(walk!, leg);
        Assert.NotEmpty(walkKeys);

        foreach (string name in directional)
        {
            Assert.Single(data.Model.LogicalAnimations.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)));
            Assert.True(data.Skeleton.TryGetClip(name, out Animation? clip));
            System.Numerics.Quaternion[] keys = LegRotationKeys(clip!, leg);
            Assert.NotEmpty(keys);
            Assert.NotEqual(walkKeys, keys);
        }
    }

    private static System.Numerics.Quaternion[] LegRotationKeys(Animation clip, Node node) =>
        clip.FindRotationChannel(node)?.GetRotationSampler().GetLinearKeys().Select(k => k.Value).ToArray() ?? [];

    // The villagers stand at the player's height, not the 1.64 m boxes they replace, and the height
    // is MEASURED from the asset rather than a hard-coded authored constant.
    [Theory]
    [MemberData(nameof(NpcModelPaths))]
    public void TheVillagers_NormalizeToThePlayersHeight(string modelPath)
    {
        WorldAssetDimensions authored = WorldAssetBounds.MeasureDimensions(modelPath);
        WorldAssetNormalization normalization = new(
            authored,
            new WorldAssetDimensions(NpcAssetDefinition.TargetWorldHeight, 1f, 1f));

        Assert.Equal(PlayerModelAssetDefinition.TargetWorldDimensions.Height, NpcAssetDefinition.TargetWorldHeight, 3);
        Assert.True(authored.Height > 0f);
        Assert.Equal(
            NpcAssetDefinition.TargetWorldHeight,
            authored.Height * normalization.ResolveUniformScale(),
            3);
    }

    // The point of BeastAssetDefinition: the authored height is MEASURED from the GLB, not a
    // hard-coded constant like the wolf's 3.191f, so re-forging the asset cannot silently change
    // the world height it draws at.
    [Fact]
    public void TheHound_MeasuresItsOwnAuthoredHeight_AndNormalizesToTheWolfsWorldHeight()
    {
        WorldAssetDimensions measured = WorldAssetBounds.MeasureDimensions(BeastAssetDefinition.HoundModelPath);

        Assert.True(measured.Height > 0f);
        Assert.Equal(measured.Height, BeastAssetDefinition.HoundNormalization.AuthoredHeight, 3);
        Assert.Equal(
            WolfAssetDefinition.Normalization.TargetWorldHeight,
            BeastAssetDefinition.TargetWorldHeight,
            3);
        Assert.Equal(
            BeastAssetDefinition.TargetWorldHeight,
            measured.Height * BeastAssetDefinition.HoundVisualScale,
            3);
    }

    // Until the asset is forged, the measurement must not throw inside a draw: a zero authored
    // height resolves to a uniform scale of 1 (WorldAssetNormalization already does this).
    [Fact]
    public void ResolvingTheHoundsScale_NeverThrows_EvenBeforeTheAssetIsForged()
    {
        float scale = 0f;
        Exception? thrown = Record.Exception(() => scale = BeastAssetDefinition.HoundVisualScale);

        Assert.Null(thrown);
        Assert.True(scale > 0f);
    }

    private static Matrix4 Weighted(
        Matrix4[] jointMatrices,
        System.Numerics.Vector4 joints,
        System.Numerics.Vector4 weights)
    {
        Matrix4 result = Matrix4.Zero;
        for (int i = 0; i < 4; i++)
        {
            float weight = Component(weights, i);
            if (weight <= 0f)
            {
                continue;
            }

            result += jointMatrices[(int)MathF.Round(Component(joints, i))] * weight;
        }

        return result;
    }

    private static float Component(System.Numerics.Vector4 vector, int index) => index switch
    {
        0 => vector.X,
        1 => vector.Y,
        2 => vector.Z,
        _ => vector.W
    };
}
