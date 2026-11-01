using OpenTK.Mathematics;
using Dream.Simulation.World;
using Dream.Simulation.Combat;

namespace Dream.Rendering.Hud;


public sealed class FloatingCombatTextSystem
{
    public const int DefaultPoolSize = 150;
    public const int MaxActivePerTarget = 12;

    private readonly Entry[] _entries;
    private readonly Random _random = new(1337);
    private long _spawnCounter;

    public FloatingCombatTextSystem(int capacity = DefaultPoolSize)
    {
        _entries = new Entry[Math.Max(capacity, MaxActivePerTarget)];
        for (int i = 0; i < _entries.Length; i++)
        {
            _entries[i] = new Entry();
        }
    }

    public int ActiveCount => _entries.Count(entry => entry.Active);

    public void Spawn(int targetId, Vector3 worldAnchor, int amount, FloatingCombatTextKind kind)
    {
        // At the cap the target's oldest number makes room for the new one, so the newest hit is
        // never the one that goes missing.
        if (GetActiveCountForTarget(targetId) >= MaxActivePerTarget)
        {
            Entry? oldest = FindOldestForTarget(targetId);
            if (oldest is not null)
            {
                oldest.Active = false;
            }
        }

        Entry? entry = FindInactiveEntry();
        if (entry is null)
        {
            return;
        }

        entry.Active = true;
        entry.TargetId = targetId;
        entry.WorldAnchor = worldAnchor;
        entry.Text = amount.ToString();
        entry.Kind = kind;
        entry.AgeSeconds = 0f;
        entry.SpawnOrder = ++_spawnCounter;
        entry.HorizontalOffset = RandomHorizontalOffset();
    }

    public void Update(float deltaTime)
    {
        Update(deltaTime, FloatingCombatTextTuning.Default);
    }

    public void Update(float deltaTime, FloatingCombatTextTuning tuning)
    {
        float lifetimeSeconds = FloatingCombatTextMotion.ResolveLifetimeSeconds(tuning.Clamp().MotionSpeedMultiplier);
        foreach (Entry entry in _entries)
        {
            if (!entry.Active)
            {
                continue;
            }

            entry.AgeSeconds += deltaTime;
            if (entry.AgeSeconds >= lifetimeSeconds)
            {
                entry.Active = false;
            }
        }
    }

    public void CollectRenderStates(List<FloatingCombatTextRenderState> destination)
    {
        CollectRenderStates(destination, FloatingCombatTextTuning.Default);
    }

    public void CollectRenderStates(List<FloatingCombatTextRenderState> destination, FloatingCombatTextTuning tuning)
    {
        destination.Clear();
        FloatingCombatTextTuning resolvedTuning = tuning.Clamp();

        foreach (Entry entry in _entries)
        {
            if (!entry.Active)
            {
                continue;
            }

            FloatingCombatTextStyle style = FloatingCombatTextStyles.Resolve(entry.Kind);
            float motionAgeSeconds = entry.AgeSeconds * resolvedTuning.MotionSpeedMultiplier;
            float alpha = FloatingCombatTextMotion.EvaluateAlpha(motionAgeSeconds);
            if (alpha <= 0f)
            {
                continue;
            }

            float maxDepthOffset = FloatingCombatTextMotion.MaxDepthOffset;
            if (entry.Kind == FloatingCombatTextKind.Critical)
            {
                maxDepthOffset *= resolvedTuning.CriticalDepthMultiplier;
            }

            destination.Add(new FloatingCombatTextRenderState(
                entry.Text,
                entry.WorldAnchor,
                entry.HorizontalOffset,
                FloatingCombatTextMotion.EvaluateVerticalOffset(motionAgeSeconds),
                FloatingCombatTextMotion.EvaluateDepthOffset(motionAgeSeconds, maxDepthOffset),
                FloatingCombatTextMotion.EvaluateScale(motionAgeSeconds, style.ScaleMultiplier * resolvedTuning.SizeMultiplier),
                alpha,
                style,
                style.LayerPriority,
                entry.Kind,
                entry.AgeSeconds * resolvedTuning.MotionSpeedMultiplier / FloatingCombatTextMotion.ClassicReferenceMotionSpeed,
                entry.SpawnOrder,
                entry.TargetId,
                resolvedTuning.SizeMultiplier / FloatingCombatTextMotion.ClassicReferenceSizeMultiplier));
        }

        destination.Sort(static (left, right) => left.LayerPriority.CompareTo(right.LayerPriority));
    }

    private int GetActiveCountForTarget(int targetId)
    {
        int count = 0;
        foreach (Entry entry in _entries)
        {
            if (entry.Active && entry.TargetId == targetId)
            {
                count++;
            }
        }

        return count;
    }

    private Entry? FindOldestForTarget(int targetId)
    {
        Entry? oldest = null;
        foreach (Entry entry in _entries)
        {
            if (entry.Active && entry.TargetId == targetId && (oldest is null || entry.SpawnOrder < oldest.SpawnOrder))
            {
                oldest = entry;
            }
        }

        return oldest;
    }

    private Entry? FindInactiveEntry()
    {
        foreach (Entry entry in _entries)
        {
            if (!entry.Active)
            {
                return entry;
            }
        }

        return null;
    }

    private float RandomHorizontalOffset()
    {
        return ((float)_random.NextDouble() * 0.70f) - 0.35f;
    }

    private sealed class Entry
    {
        public bool Active { get; set; }

        public int TargetId { get; set; }

        public Vector3 WorldAnchor { get; set; }

        public string Text { get; set; } = string.Empty;

        public FloatingCombatTextKind Kind { get; set; }

        public float AgeSeconds { get; set; }

        public long SpawnOrder { get; set; }

        public float HorizontalOffset { get; set; }
    }
}
