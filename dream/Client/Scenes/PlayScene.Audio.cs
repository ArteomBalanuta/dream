using OpenTK.Mathematics;
using Dream.Client.Audio;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Actors;
using Dream.Rendering.Effects;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Client.Scenes;

// Sound for the play scene. The rule: local input sounds (swing, jump, steps, drinking, UI) play
// from the prediction; combat, death, block and loot sounds play only from authoritative data, so
// nothing is heard twice. Nothing here can throw into a frame: GameAudio.Current is always safe.
public partial class PlayScene
{
    private const float LandMinFallSpeed = 4f;
    private const float EnemyStepHearingRange = 30f;
    private const float EnemyGrowlRange = 8f;
    private const int MaxMutationSoundsPerSnapshot = 6;
    private const int LevelUpArmFrames = 180;

    private AudioEventBridge? _audioBridge;
    private readonly FootstepCadence _localFootsteps = new();
    private readonly Dictionary<int, AudioWalker> _audioRemoteWalkers = new();
    private readonly Dictionary<int, AudioWalker> _audioEnemyWalkers = new();
    private readonly Random _audioRandom = new();
    private int _audioFrame;
    private bool _audioGrounded = true;
    private bool _audioHasFeet;
    private float _audioLastFeetY;
    private float _audioFallSpeed;
    private bool _audioJumpHeld;
    private bool _audioWasInventoryVisible;
    private bool _audioWasDead;
    private int _audioLastLevel;
    private float _audioHowlSeconds = 30f;
    private EquipmentPresentation? _audioLastEquipment;

    private sealed class AudioWalker(float walkStride)
    {
        public FootstepCadence Steps { get; } = new(walkStride, walkStride * 1.3f, 7f);

        public Vector3 LastPosition;
        public bool HasLast;
        public float GrowlCooldown;
        public int Frame;
    }

    private void InitializeAudio()
    {
        GameAudio.EnsureStarted(AudioVolumes.FromConfig(_desktopClientConfig));
        _audioBridge = new AudioEventBridge(_events, CurrentGameAudio.Instance, () => _runtime.Player.Combatant.Actor.Id);
        CurrentGameAudio.Instance.SetMusic(MusicTrack.World);
    }

    private void DisposeAudio()
    {
        // The bridge goes first so no event reaches the audio after the scene is gone.
        _audioBridge?.Dispose();
        _audioBridge = null;
        CurrentGameAudio.Instance.SetMusic(MusicTrack.None);
    }

    // Called once per frame from Render, after the presentation capture.
    private void UpdateAudioFrame(
        WorldSessionPresentationState presentation,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        float night01,
        float delta)
    {
        IGameAudio audio = CurrentGameAudio.Instance;
        if (!audio.IsActive || delta <= 0f)
        {
            return;
        }

        _audioFrame++;
        Vector3 feet = FxShadowRules.PlayerFeet(_runtime.LocalRenderPose.Position);
        audio.SetListener(feet, presentation.Camera.Right);
        bool dead = _deathTracker.IsDead;

        UpdateLocalMovementAudio(audio, feet, dead, delta);
        UpdateRemoteWalkerAudio(audio, remotePlayers, delta);
        UpdateEnemyWalkerAudio(audio, presentation.ActiveEnemies, feet, delta);

        bool inventoryVisible = _settings.InventoryVisible;
        if (inventoryVisible != _audioWasInventoryVisible)
        {
            audio.Play(inventoryVisible ? SoundId.InventoryOpen : SoundId.InventoryClose);
            _audioWasInventoryVisible = inventoryVisible;
        }

        if (_audioWasDead && !dead)
        {
            audio.Play(SoundId.Respawn);
        }

        _audioWasDead = dead;
        int level = presentation.HudStatus.Level;
        // The level starts at 1 until the first progression snapshot lands, so a join above level 1 must
        // not sound: arm after a settle period, and only a rise by exactly one is a level-up.
        if (_audioFrame > LevelUpArmFrames && _audioLastLevel > 0 && level == _audioLastLevel + 1)
        {
            audio.Play(SoundId.LevelUp);
        }

        _audioLastLevel = level;
        audio.SetAmbience(night01);
        if (night01 > 0.5f)
        {
            _audioHowlSeconds -= delta;
            if (_audioHowlSeconds <= 0f)
            {
                audio.Play(SoundId.WolfHowl, 0.45f);
                _audioHowlSeconds = 25f + (_audioRandom.NextSingle() * 35f);
            }
        }
    }

    private void UpdateLocalMovementAudio(IGameAudio audio, Vector3 feet, bool dead, float delta)
    {
        bool grounded = FxGroundProbe.TryFindGroundY(_runtime.Map, feet, out float groundY) && feet.Y - groundY < 0.3f;
        if (_audioHasFeet)
        {
            float vy = (feet.Y - _audioLastFeetY) / delta;
            if (MathF.Abs(vy) > 60f)
            {
                // A respawn or snap, not a fall.
                _audioFallSpeed = 0f;
            }
            else if (!grounded)
            {
                _audioFallSpeed = MathF.Min(_audioFallSpeed, vy);
            }
            else if (!_audioGrounded)
            {
                if (-_audioFallSpeed > LandMinFallSpeed && !dead)
                {
                    audio.Play(SoundId.Land, Math.Clamp(-_audioFallSpeed / 12f, 0.5f, 1f));
                }

                _audioFallSpeed = 0f;
            }
        }

        _audioLastFeetY = feet.Y;
        _audioHasFeet = true;
        _audioGrounded = grounded;
        if (_localFootsteps.Advance(feet, grounded, delta) && !dead)
        {
            audio.Play(SoundId.Footstep, 0.8f, Jitter(0.07f));
        }
    }

    private void UpdateRemoteWalkerAudio(IGameAudio audio, IReadOnlyList<RemotePlayerPresentationState> remotePlayers, float delta)
    {
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            RemotePlayerPresentationState remote = remotePlayers[i];
            if (!_audioRemoteWalkers.TryGetValue(remote.ActorId, out AudioWalker? walker))
            {
                walker = new AudioWalker(FootstepCadence.WalkStride);
                _audioRemoteWalkers[remote.ActorId] = walker;
            }

            walker.Frame = _audioFrame;
            Vector3 feet = FxShadowRules.PlayerFeet(remote.Pose.Position);
            if (walker.Steps.Advance(feet, !remote.Airborne && remote.IsAlive, delta))
            {
                audio.PlayAt(SoundId.Footstep, feet, 0.7f, Jitter(0.07f));
            }
        }

        PruneWalkers(_audioRemoteWalkers);
    }

    private void UpdateEnemyWalkerAudio(IGameAudio audio, IReadOnlyList<EnemyActor> enemies, Vector3 listenerFeet, float delta)
    {
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor enemy = enemies[i];
            Vector3 position = EnemyRenderer.ResolveRenderPose(enemy, _scenePresenter, RemotePlayerRenderTick).Position;
            float distance = (position - listenerFeet).Length;
            if (distance > EnemyStepHearingRange || !enemy.Combatant.Health.IsAlive)
            {
                continue;
            }

            int id = enemy.Combatant.Actor.Id;
            if (!_audioEnemyWalkers.TryGetValue(id, out AudioWalker? walker))
            {
                walker = new AudioWalker(1.3f);
                _audioEnemyWalkers[id] = walker;
            }

            walker.Frame = _audioFrame;
            bool moving = walker.HasLast && (position - walker.LastPosition).LengthSquared > 1e-6f;
            walker.LastPosition = position;
            walker.HasLast = true;
            if (walker.Steps.Advance(position, true, delta))
            {
                audio.PlayAt(SoundId.Footstep, position, 0.5f, Jitter(0.1f) * 0.85f);
            }

            walker.GrowlCooldown -= delta;
            if (moving && distance < EnemyGrowlRange && walker.GrowlCooldown <= 0f
                && string.Equals(enemy.EnemyKind, "Wolf", StringComparison.OrdinalIgnoreCase))
            {
                audio.PlayAt(SoundId.WolfGrowl, position, 0.8f);
                walker.GrowlCooldown = 6f + (_audioRandom.NextSingle() * 8f);
            }
        }

        PruneWalkers(_audioEnemyWalkers);
    }

    // Drops walkers not seen for a while, so a dead or distant actor does not keep an entry forever.
    private void PruneWalkers(Dictionary<int, AudioWalker> walkers)
    {
        if (walkers.Count == 0 || (_audioFrame & 127) != 0)
        {
            return;
        }

        List<int>? stale = null;
        foreach ((int id, AudioWalker walker) in walkers)
        {
            if (_audioFrame - walker.Frame > 120)
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (int id in stale)
        {
            walkers.Remove(id);
        }
    }

    private float Jitter(float spread) => 1f + (((_audioRandom.NextSingle() * 2f) - 1f) * spread);

    // The jump key edge while standing: the prediction's own rule, so no jump sound in the air.
    private void PlayJumpSoundIfPressed(bool jumpPressed)
    {
        bool edge = jumpPressed && !_audioJumpHeld;
        _audioJumpHeld = jumpPressed;
        if (edge && _audioGrounded)
        {
            CurrentGameAudio.Instance.Play(SoundId.Jump, 0.8f, Jitter(0.05f));
        }
    }

    private void PlaySwingSound()
    {
        CurrentGameAudio.Instance.Play(SoundId.Swing, 0.9f, Jitter(0.06f));
    }

    // Block sounds from the snapshot's world deltas, before they are applied. A full world snapshot
    // and a delta already applied (the resend after a resync) stay silent.
    private void PlayWorldMutationSounds(ServerSnapshotEnvelope snapshot)
    {
        IGameAudio audio = CurrentGameAudio.Instance;
        if (!audio.IsActive || snapshot.World != null || snapshot.WorldMutations.Count == 0)
        {
            return;
        }

        ulong applied = _runtime.LastAppliedReplicatedWorldMutationSequence;
        int played = 0;
        for (int i = 0; i < snapshot.WorldMutations.Count && played < MaxMutationSoundsPerSnapshot; i++)
        {
            WorldMutationDelta mutation = snapshot.WorldMutations[i];
            if (mutation.Sequence != 0 && mutation.Sequence <= applied)
            {
                continue;
            }

            SoundId? sound = mutation.Kind switch
            {
                WorldMutationKind.CubePlaced => SoundId.BlockPlace,
                WorldMutationKind.CubeDestroyed => SoundId.BlockBreak,
                WorldMutationKind.CubeFractured => SoundId.BlockCrack,
                _ => null
            };
            if (sound is SoundId id)
            {
                audio.PlayAt(id, mutation.CellPosition + new Vector3(0.5f), 1f, Jitter(0.06f));
                played++;
            }
        }
    }

    // Equip or unequip sound when what the hands show changes (not for the first snapshot).
    private void ObserveEquipmentForAudio(EquipmentPresentation current)
    {
        // Worn armour is not compared: only what the hands show and the held light make the sound.
        if (_audioLastEquipment is EquipmentPresentation previous && previous with { Visuals = default } != current with { Visuals = default })
        {
            bool holding = current.MainHandVisualId != ViewModelAssets.EmptyHeldItemId || current.OffHandVisualId != null;
            CurrentGameAudio.Instance.Play(holding ? SoundId.Equip : SoundId.Unequip);
        }

        _audioLastEquipment = current;
    }
}
