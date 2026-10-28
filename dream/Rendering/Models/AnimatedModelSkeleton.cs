using System.Collections.Concurrent;
using SharpGLTF.Schema2;

namespace Dream.Rendering.Models;

// The shared, read-only half of an animated model: the node hierarchy, the bind (rest) pose, the
// clips, the skin joints and the keyframe tracks of every clip. It never changes after it is
// built, so one skeleton is shared by every AnimatedModelInstance of a model, on any thread.
// Per-actor state (clip, time, pose matrices, joint matrices) lives in AnimatedModelInstance and
// AnimatedModelPose. It has no GL dependency; the GPU buffers that go with it belong to the
// AnimatedModel that uploads them.
//
// SharpGLTF builds new curve samplers on every Node.GetLocalTransform(animation, time) call, its
// linear samplers allocate on every GetPoint, and Node.WorldMatrix allocates while it walks the
// parents. So the skeleton keeps its own copy of the hierarchy and the keyframes, and leaves the
// glTF nodes at their loaded state.
public sealed class AnimatedModelSkeleton
{
    private static readonly IReadOnlyDictionary<string, Animation> NoClips = new Dictionary<string, Animation>();

    private readonly Node[] _nodes;
    private readonly int[] _parentIndices;
    private readonly int[] _hierarchyOrder;
    private readonly System.Numerics.Vector3[] _restScales;
    private readonly System.Numerics.Quaternion[] _restRotations;
    private readonly System.Numerics.Vector3[] _restTranslations;
    private readonly System.Numerics.Matrix4x4[] _restLocalMatrices;
    private readonly Node[] _renderableNodes;
    private readonly int[] _renderableNodeIndices;
    private readonly AnimatedModelSkinJoint[] _skinJoints;
    // SharpGLTF's Animation.Duration walks the channels and allocates on every read, so each
    // clip's duration is read once here.
    private readonly Dictionary<string, (Animation Clip, float Duration)> _clipsByName = new(StringComparer.Ordinal);

    // Built for every clip of the model up front. A clip from outside the model (only tests do
    // that) is built on first use; ConcurrentDictionary keeps that safe for concurrent readers
    // and its TryGetValue never allocates.
    private readonly ConcurrentDictionary<Animation, AnimatedNodeTrack[]> _tracksByAnimation = new();

    // Builds the rig of a glTF model with no clips registered by name and no skin; used where only
    // the pose is needed.
    public AnimatedModelSkeleton(ModelRoot model)
        : this(model, NoClips, skinJoints: null)
    {
    }

    public AnimatedModelSkeleton(
        ModelRoot model,
        IReadOnlyDictionary<string, Animation> clips,
        IReadOnlyList<AnimatedModelSkinJoint>? skinJoints)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(clips);

        Clips = clips;
        foreach (KeyValuePair<string, Animation> clip in clips)
        {
            _clipsByName[clip.Key] = (clip.Value, clip.Value.Duration);
        }

        _skinJoints = skinJoints?.ToArray() ?? [];
        HasSkin = skinJoints != null;

        _nodes = model.LogicalNodes.ToArray();
        int nodeCount = _nodes.Length;
        _parentIndices = new int[nodeCount];
        _restScales = new System.Numerics.Vector3[nodeCount];
        _restRotations = new System.Numerics.Quaternion[nodeCount];
        _restTranslations = new System.Numerics.Vector3[nodeCount];
        _restLocalMatrices = new System.Numerics.Matrix4x4[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            Node node = _nodes[i];
            _parentIndices[i] = node.VisualParent?.LogicalIndex ?? -1;
            _restLocalMatrices[i] = node.LocalMatrix;

            SharpGLTF.Transforms.AffineTransform localTransform = node.LocalTransform;
            if (localTransform.IsMatrix)
            {
                System.Numerics.Matrix4x4.Decompose(
                    localTransform.Matrix,
                    out _restScales[i],
                    out _restRotations[i],
                    out _restTranslations[i]);
            }
            else
            {
                _restScales[i] = localTransform.Scale;
                _restRotations[i] = localTransform.Rotation;
                _restTranslations[i] = localTransform.Translation;
            }
        }

        _hierarchyOrder = CreateHierarchyOrder(_parentIndices);

        List<Node> renderableNodes = [];
        SceneNodeTraversal.CollectRenderableNodes(model.DefaultScene.VisualChildren, renderableNodes);
        _renderableNodes = renderableNodes.ToArray();
        _renderableNodeIndices = new int[_renderableNodes.Length];
        for (int i = 0; i < _renderableNodes.Length; i++)
        {
            _renderableNodeIndices[i] = _renderableNodes[i].LogicalIndex;
        }

        foreach (Animation animation in model.LogicalAnimations)
        {
            _tracksByAnimation[animation] = CreateTracks(animation);
        }
    }

    public int NodeCount => _nodes.Length;

    public int RenderableNodeCount => _renderableNodes.Length;

    // Clips by name, as loaded (AnimatedModelData.Animations).
    public IReadOnlyDictionary<string, Animation> Clips { get; }

    // False when the model has no skin or its skin exceeds AnimatedModelData.MaxSupportedJoints.
    public bool HasSkin { get; }

    public int JointCount => _skinJoints.Length;

    public ReadOnlySpan<AnimatedModelSkinJoint> SkinJoints => _skinJoints;

    public Node GetRenderableNode(int renderableIndex) => _renderableNodes[renderableIndex];

    public int GetRenderableNodeLogicalIndex(int renderableIndex) => _renderableNodeIndices[renderableIndex];

    public bool TryGetClip(string clipName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Animation? clip) =>
        TryGetClip(clipName, out clip, out _);

    // The clip and its duration (Animation.Duration, read once at build). Allocates nothing.
    public bool TryGetClip(
        string clipName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Animation? clip,
        out float durationSeconds)
    {
        if (_clipsByName.TryGetValue(clipName, out (Animation Clip, float Duration) entry))
        {
            clip = entry.Clip;
            durationSeconds = entry.Duration;
            return true;
        }

        clip = null;
        durationSeconds = 0f;
        return false;
    }

    internal int[] HierarchyOrder => _hierarchyOrder;

    internal int[] ParentIndices => _parentIndices;

    // Copies the rest pose into a pose's per-node arrays.
    internal void CopyRestPose(
        System.Numerics.Vector3[] scales,
        System.Numerics.Quaternion[] rotations,
        System.Numerics.Vector3[] translations,
        System.Numerics.Matrix4x4[] localMatrices)
    {
        Array.Copy(_restScales, scales, _restScales.Length);
        Array.Copy(_restRotations, rotations, _restRotations.Length);
        Array.Copy(_restTranslations, translations, _restTranslations.Length);
        Array.Copy(_restLocalMatrices, localMatrices, _restLocalMatrices.Length);
    }

    internal AnimatedNodeTrack[] GetTracks(Animation animation)
    {
        if (_tracksByAnimation.TryGetValue(animation, out AnimatedNodeTrack[]? tracks))
        {
            return tracks;
        }

        return _tracksByAnimation.GetOrAdd(animation, CreateTracks(animation));
    }

    private AnimatedNodeTrack[] CreateTracks(Animation animation)
    {
        List<AnimatedNodeTrack> tracks = [];
        for (int i = 0; i < _nodes.Length; i++)
        {
            Node node = _nodes[i];
            if (!animation.FindChannels(node).Any())
            {
                continue;
            }

            tracks.Add(new AnimatedNodeTrack(
                i,
                KeyframeCurve<System.Numerics.Vector3>.Create(
                    animation.FindScaleChannel(node)?.GetScaleSampler(),
                    static (a, b, amount) => System.Numerics.Vector3.Lerp(a, b, amount)),
                KeyframeCurve<System.Numerics.Quaternion>.Create(
                    animation.FindRotationChannel(node)?.GetRotationSampler(),
                    static (a, b, amount) => System.Numerics.Quaternion.Slerp(a, b, amount)),
                KeyframeCurve<System.Numerics.Vector3>.Create(
                    animation.FindTranslationChannel(node)?.GetTranslationSampler(),
                    static (a, b, amount) => System.Numerics.Vector3.Lerp(a, b, amount))));
        }

        return tracks.ToArray();
    }

    private static int[] CreateHierarchyOrder(int[] parentIndices)
    {
        int[] order = new int[parentIndices.Length];
        bool[] placed = new bool[parentIndices.Length];
        int count = 0;
        for (int i = 0; i < parentIndices.Length; i++)
        {
            count = PlaceWithAncestors(i, parentIndices, placed, order, count);
        }

        return order;
    }

    private static int PlaceWithAncestors(int nodeIndex, int[] parentIndices, bool[] placed, int[] order, int count)
    {
        if (placed[nodeIndex])
        {
            return count;
        }

        int parentIndex = parentIndices[nodeIndex];
        if (parentIndex >= 0)
        {
            count = PlaceWithAncestors(parentIndex, parentIndices, placed, order, count);
        }

        placed[nodeIndex] = true;
        order[count] = nodeIndex;
        return count + 1;
    }

    internal sealed class AnimatedNodeTrack(
        int nodeIndex,
        KeyframeCurve<System.Numerics.Vector3>? scale,
        KeyframeCurve<System.Numerics.Quaternion>? rotation,
        KeyframeCurve<System.Numerics.Vector3>? translation)
    {
        public int NodeIndex { get; } = nodeIndex;
        public KeyframeCurve<System.Numerics.Vector3>? Scale { get; } = scale;
        public KeyframeCurve<System.Numerics.Quaternion>? Rotation { get; } = rotation;
        public KeyframeCurve<System.Numerics.Vector3>? Translation { get; } = translation;
    }

    // One animation channel as flat key and value arrays. LINEAR and STEP sample with a binary
    // search and no allocations. CUBICSPLINE (unused by the current assets) keeps SharpGLTF's own
    // curve sampler, which is correct but allocates per sample.
    internal sealed class KeyframeCurve<T>
        where T : struct
    {
        private readonly float[] _times;
        private readonly T[] _values;
        private readonly bool _step;
        private readonly Func<T, T, float, T> _interpolate;
        private readonly SharpGLTF.Animations.ICurveSampler<T>? _cubicSampler;

        private KeyframeCurve(
            float[] times,
            T[] values,
            bool step,
            Func<T, T, float, T> interpolate,
            SharpGLTF.Animations.ICurveSampler<T>? cubicSampler)
        {
            _times = times;
            _values = values;
            _step = step;
            _interpolate = interpolate;
            _cubicSampler = cubicSampler;
        }

        public static KeyframeCurve<T>? Create(IAnimationSampler<T>? sampler, Func<T, T, float, T> interpolate)
        {
            if (sampler == null)
            {
                return null;
            }

            if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
            {
                return new KeyframeCurve<T>([], [], step: false, interpolate, sampler.CreateCurveSampler());
            }

            (float Key, T Value)[] keys = sampler.GetLinearKeys().ToArray();
            if (keys.Length == 0)
            {
                return null;
            }

            float[] times = new float[keys.Length];
            T[] values = new T[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                times[i] = keys[i].Key;
                values[i] = keys[i].Value;
            }

            return new KeyframeCurve<T>(
                times,
                values,
                sampler.InterpolationMode == AnimationInterpolationMode.STEP,
                interpolate,
                cubicSampler: null);
        }

        public T Sample(float time)
        {
            if (_cubicSampler != null)
            {
                return _cubicSampler.GetPoint(time);
            }

            int last = _times.Length - 1;
            if (time <= _times[0])
            {
                return _values[0];
            }

            if (time >= _times[last])
            {
                return _values[last];
            }

            // Largest key index whose time is at or before the sample time.
            int low = 0;
            int high = last;
            while (high - low > 1)
            {
                int middle = (low + high) >> 1;
                if (_times[middle] <= time)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            if (_step)
            {
                return _values[low];
            }

            float amount = (time - _times[low]) / (_times[high] - _times[low]);
            return _interpolate(_values[low], _values[high], amount);
        }
    }
}
