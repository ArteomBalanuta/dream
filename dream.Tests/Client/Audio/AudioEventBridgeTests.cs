using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Audio;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Client.Audio;

public class AudioEventBridgeTests
{
    private const int LocalId = 1;

    private sealed class RecordingAudio : IGameAudio
    {
        public List<(SoundId Id, Vector3? Position)> Played { get; } = [];

        public bool IsActive => true;

        public AudioVolumes Volumes => AudioVolumes.Default;

        public void Play(SoundId id, float volume = 1f, float pitch = 1f) => Played.Add((id, null));

        public void PlayAt(SoundId id, Vector3 position, float volume = 1f, float pitch = 1f) => Played.Add((id, position));

        public void SetListener(Vector3 position, Vector3 right)
        {
        }

        public void SetMusic(MusicTrack track)
        {
        }

        public void SetAmbience(float night01)
        {
        }

        public void SetVolumes(AudioVolumes volumes)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedAnchor(Vector3 position) : ICombatAnchorProvider
    {
        public Vector3 GetCombatAnchorWorldPosition() => position;
    }

    private static CombatActorHandle Actor(int id, CreatureKind kind, Vector3 position) =>
        new(id, kind, new HealthState(100), new FixedAnchor(position));

    private static (SceneEventHub Events, RecordingAudio Audio, AudioEventBridge Bridge) Create()
    {
        SceneEventHub events = new();
        RecordingAudio audio = new();
        return (events, audio, new AudioEventBridge(events, audio, () => LocalId));
    }

    [Fact]
    public void Regular_Hit_On_An_Enemy_Plays_Hit_And_Yelp_At_Its_Position()
    {
        (SceneEventHub events, RecordingAudio audio, _) = Create();
        Vector3 at = new(3, 1, 4);

        events.Publish(new DamageAppliedEvent(Actor(7, CreatureKind.Enemy, at), 12, FloatingCombatTextKind.Regular));

        Assert.Contains((SoundId.Hit, (Vector3?)at), audio.Played);
        Assert.Contains((SoundId.WolfHurt, (Vector3?)at), audio.Played);
        Assert.DoesNotContain(audio.Played, p => p.Id == SoundId.HitCritical);
    }

    [Fact]
    public void Critical_Hit_Plays_The_Crunch_Instead_Of_The_Thud()
    {
        (SceneEventHub events, RecordingAudio audio, _) = Create();

        events.Publish(new DamageAppliedEvent(Actor(7, CreatureKind.Enemy, Vector3.One), 30, FloatingCombatTextKind.Critical));

        Assert.Contains(audio.Played, p => p.Id == SoundId.HitCritical);
        Assert.DoesNotContain(audio.Played, p => p.Id == SoundId.Hit);
    }

    [Fact]
    public void Damage_To_The_Local_Player_Plays_Only_The_Hurt_Sound_Without_A_Position()
    {
        (SceneEventHub events, RecordingAudio audio, _) = Create();

        events.Publish(new DamageAppliedEvent(Actor(LocalId, CreatureKind.Player, Vector3.Zero), 5, FloatingCombatTextKind.Incoming));

        Assert.Equal([(SoundId.PlayerHurt, (Vector3?)null)], audio.Played);
    }

    [Fact]
    public void Zero_Damage_Is_Silent()
    {
        (SceneEventHub events, RecordingAudio audio, _) = Create();

        events.Publish(new DamageAppliedEvent(Actor(7, CreatureKind.Enemy, Vector3.One), 0, FloatingCombatTextKind.Regular));

        Assert.Empty(audio.Played);
    }

    [Fact]
    public void Deaths_Pick_The_Sound_By_Actor()
    {
        (SceneEventHub events, RecordingAudio audio, _) = Create();

        events.Publish(new ActorDiedEvent(Actor(7, CreatureKind.Enemy, new Vector3(1, 2, 3))));
        events.Publish(new ActorDiedEvent(Actor(LocalId, CreatureKind.Player, Vector3.Zero)));
        events.Publish(new ActorDiedEvent(Actor(9, CreatureKind.Player, new Vector3(5, 0, 5))));

        Assert.Equal(
            [(SoundId.WolfDeath, (Vector3?)new Vector3(1, 2, 3)), (SoundId.PlayerDeath, null), (SoundId.PlayerDeath, (Vector3?)new Vector3(5, 0, 5))],
            audio.Played);
    }

    [Fact]
    public void Dispose_Stops_Listening()
    {
        (SceneEventHub events, RecordingAudio audio, AudioEventBridge bridge) = Create();
        bridge.Dispose();

        events.Publish(new ActorDiedEvent(Actor(7, CreatureKind.Enemy, Vector3.One)));

        Assert.Empty(audio.Played);
    }

    [Fact]
    public void An_Event_Is_Heard_Once_Per_Bridge()
    {
        (SceneEventHub events, RecordingAudio audio, _) = Create();

        events.Publish(new DamageAppliedEvent(Actor(7, CreatureKind.Enemy, Vector3.One), 12, FloatingCombatTextKind.Regular));

        Assert.Equal(1, audio.Played.Count(p => p.Id == SoundId.Hit));
    }
}
