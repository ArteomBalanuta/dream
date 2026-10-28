using OpenTK.Mathematics;
using SharpGLTF.Schema2;

namespace Dream.Rendering.Models;

// One actor's animation state over a shared AnimatedModelSkeleton: the playing clip, its time, the
// idle clip a one-shot returns to, the pose, and the actor's own joint-matrix array. It owns no GL
// resources, so it needs no disposal; the GPU buffers belong to the shared AnimatedModel that draws
// it (AnimatedModel.RenderPrepared(shader, instance, ...)). Everything is allocated in the
// constructor; Update and ComputeJointMatrices allocate nothing.
//
// The owner calls Update exactly once per frame with that frame's dt, so every actor's clip
// advances by its own time: N actors over one second each reach one second, not N.
public sealed class AnimatedModelInstance
{
    private readonly Matrix4[] _jointMatrices;
    private AnimationPlaybackState _playback = AnimationPlaybackState.Stopped;
    private string? _idleClipName;

    public AnimatedModelInstance(AnimatedModelSkeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        Pose = new AnimatedModelPose(skeleton);
        _jointMatrices = new Matrix4[skeleton.JointCount];
    }

    public AnimatedModelSkeleton Skeleton { get; }

    public AnimatedModelPose Pose { get; }

    public AnimationPlaybackState Playback => _playback;

    public string? CurrentClipName => _playback.ClipName;

    public float TimeSeconds => _playback.TimeSeconds;

    public float AnimationSpeed { get; set; } = 1.0f;

    // The joint matrices from the last ComputeJointMatrices call; one per skin joint.
    public ReadOnlySpan<Matrix4> JointMatrices => _jointMatrices;

    // The same array, for the single uniform-array upload in AnimatedModel.
    internal Matrix4[] JointMatrixStorage => _jointMatrices;

    // Advances the clip by deltaTime and poses the skeleton. Returns true when a one-shot finished
    // this call (it then switches back to the idle loop, if one was set).
    public bool Update(float deltaTime)
    {
        if (!_playback.IsPlaying || string.IsNullOrWhiteSpace(_playback.ClipName))
        {
            return false;
        }

        if (!Skeleton.TryGetClip(_playback.ClipName, out Animation? animation, out float duration))
        {
            _playback = AnimationPlaybackState.Stopped;
            return false;
        }

        _playback = _playback.Advance(deltaTime * AnimationSpeed, duration, out bool finished);
        Pose.Apply(animation, _playback.TimeSeconds);

        if (finished && _playback.Mode == AnimationPlaybackMode.OneShot && !string.IsNullOrWhiteSpace(_idleClipName))
        {
            _playback = _playback.Play(_idleClipName, AnimationPlaybackMode.Loop);
            return true;
        }

        return finished;
    }

    public void PlayLoop(string clipName)
    {
        _idleClipName = clipName;
        _playback = _playback.Play(clipName, AnimationPlaybackMode.Loop);
    }

    public void PlayOneShot(string clipName)
    {
        _playback = _playback.Play(clipName, AnimationPlaybackMode.OneShot);
    }

    public void StartAnimation(string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        PlayLoop(name);
    }

    public void StopAnimation()
    {
        _playback = AnimationPlaybackState.Stopped;
        _idleClipName = null;
    }

    public bool IsPlayingLoop(string clipName) =>
        _playback.IsPlaying &&
        _playback.Mode == AnimationPlaybackMode.Loop &&
        string.Equals(_playback.ClipName, clipName, StringComparison.Ordinal);

    // Back to the state of a new instance (no clip, speed 1, rest pose): used when a pooled
    // instance is handed to another actor.
    public void Reset()
    {
        StopAnimation();
        AnimationSpeed = 1.0f;
        Pose.ResetToRest();
    }

    // Fills this instance's joint matrices for a mesh whose world matrix is meshWorldMatrix and
    // returns them. Same composition as the per-joint upload it replaces.
    public ReadOnlySpan<Matrix4> ComputeJointMatrices(Matrix4 meshWorldMatrix)
    {
        ComputeJointMatrices(Skeleton, Pose, meshWorldMatrix, _jointMatrices);
        return _jointMatrices;
    }

    internal static void ComputeJointMatrices(
        AnimatedModelSkeleton skeleton,
        AnimatedModelPose pose,
        Matrix4 meshWorldMatrix,
        Matrix4[] destination)
    {
        ReadOnlySpan<AnimatedModelSkinJoint> joints = skeleton.SkinJoints;
        for (int i = 0; i < joints.Length; i++)
        {
            AnimatedModelSkinJoint joint = joints[i];
            destination[i] = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                meshWorldMatrix,
                pose.GetNodeWorldMatrix(joint.NodeIndex).ToOpenTK(),
                joint.InverseBindMatrix);
        }
    }
}

// The animated pose of one instance: a local and a world matrix per logical node over a shared
// AnimatedModelSkeleton, which holds the hierarchy, the rest pose and the keyframe tracks. It
// has no GL dependency.
public sealed class AnimatedModelPose
{
    private readonly AnimatedModelSkeleton _skeleton;
    private readonly System.Numerics.Vector3[] _scales;
    private readonly System.Numerics.Quaternion[] _rotations;
    private readonly System.Numerics.Vector3[] _translations;
    private readonly System.Numerics.Matrix4x4[] _localMatrices;
    private readonly System.Numerics.Matrix4x4[] _worldMatrices;

    // A pose with a skeleton of its own; for code that only needs one pose of a model.
    public AnimatedModelPose(ModelRoot model)
        : this(new AnimatedModelSkeleton(model))
    {
    }

    public AnimatedModelPose(AnimatedModelSkeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        _skeleton = skeleton;
        int nodeCount = skeleton.NodeCount;
        _scales = new System.Numerics.Vector3[nodeCount];
        _rotations = new System.Numerics.Quaternion[nodeCount];
        _translations = new System.Numerics.Vector3[nodeCount];
        _localMatrices = new System.Numerics.Matrix4x4[nodeCount];
        _worldMatrices = new System.Numerics.Matrix4x4[nodeCount];
        ResetToRest();
    }

    public AnimatedModelSkeleton Skeleton => _skeleton;

    public int NodeCount => _skeleton.NodeCount;

    public int RenderableNodeCount => _skeleton.RenderableNodeCount;

    public Node GetRenderableNode(int renderableIndex) => _skeleton.GetRenderableNode(renderableIndex);

    public System.Numerics.Matrix4x4 GetRenderableNodeWorldMatrix(int renderableIndex) =>
        _worldMatrices[_skeleton.GetRenderableNodeLogicalIndex(renderableIndex)];

    public System.Numerics.Matrix4x4 GetNodeLocalMatrix(int logicalIndex) => _localMatrices[logicalIndex];

    public System.Numerics.Matrix4x4 GetNodeWorldMatrix(int logicalIndex) => _worldMatrices[logicalIndex];

    public int GetAnimatedNodeCount(Animation animation) => _skeleton.GetTracks(animation).Length;

    // Back to the skeleton's rest (bind) pose.
    public void ResetToRest()
    {
        _skeleton.CopyRestPose(_scales, _rotations, _translations, _localMatrices);
        UpdateWorldMatrices();
    }

    // Same result as assigning node.GetLocalTransform(animation, time) to every node the
    // animation targets: channels the animation lacks keep the node's current value.
    public void Apply(Animation animation, float time)
    {
        AnimatedModelSkeleton.AnimatedNodeTrack[] tracks = _skeleton.GetTracks(animation);
        for (int i = 0; i < tracks.Length; i++)
        {
            AnimatedModelSkeleton.AnimatedNodeTrack track = tracks[i];
            int nodeIndex = track.NodeIndex;
            if (track.Scale != null)
            {
                _scales[nodeIndex] = track.Scale.Sample(time);
            }

            if (track.Rotation != null)
            {
                _rotations[nodeIndex] = track.Rotation.Sample(time);
            }

            if (track.Translation != null)
            {
                _translations[nodeIndex] = track.Translation.Sample(time);
            }

            _localMatrices[nodeIndex] = new SharpGLTF.Transforms.AffineTransform(
                _scales[nodeIndex],
                _rotations[nodeIndex],
                _translations[nodeIndex]).Matrix;
        }

        UpdateWorldMatrices();
    }

    private void UpdateWorldMatrices()
    {
        int[] hierarchyOrder = _skeleton.HierarchyOrder;
        int[] parentIndices = _skeleton.ParentIndices;
        for (int i = 0; i < hierarchyOrder.Length; i++)
        {
            int nodeIndex = hierarchyOrder[i];
            int parentIndex = parentIndices[nodeIndex];
            _worldMatrices[nodeIndex] = parentIndex < 0
                ? _localMatrices[nodeIndex]
                : _localMatrices[nodeIndex] * _worldMatrices[parentIndex];
        }
    }
}
