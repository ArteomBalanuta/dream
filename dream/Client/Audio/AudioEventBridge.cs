using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// Turns authoritative combat events into sound, the same shape as FxEventBridge: the anchor is read
// inside the handler, while the actor can still be resolved. Combat sound comes from here only, so
// a hit is heard once (the swing, which is predicted, is played by the scene).
public sealed class AudioEventBridge : IDisposable
{
    private readonly IGameAudio _audio;
    private readonly Func<int> _localActorId;
    private readonly IDisposable _damageSubscription;
    private readonly IDisposable _deathSubscription;

    public AudioEventBridge(SceneEventHub events, IGameAudio audio, Func<int> localActorId)
    {
        ArgumentNullException.ThrowIfNull(events);
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _localActorId = localActorId ?? throw new ArgumentNullException(nameof(localActorId));
        _damageSubscription = events.Subscribe<DamageAppliedEvent>(OnDamageApplied);
        _deathSubscription = events.Subscribe<ActorDiedEvent>(OnActorDied);
    }

    public void Dispose()
    {
        _damageSubscription.Dispose();
        _deathSubscription.Dispose();
    }

    private void OnDamageApplied(DamageAppliedEvent evt)
    {
        if (evt.Amount <= 0)
        {
            return;
        }

        try
        {
            if (evt.Actor.Id == _localActorId())
            {
                _audio.Play(SoundId.PlayerHurt);
                return;
            }

            Vector3 position = evt.Actor.GetWorldAnchor();
            bool critical = evt.Kind == FloatingCombatTextKind.Critical;
            _audio.PlayAt(critical ? SoundId.HitCritical : SoundId.Hit, position);
            if (evt.Actor.Kind == CreatureKind.Enemy)
            {
                _audio.PlayAt(SoundId.WolfHurt, position, 0.8f);
            }
            else if (evt.Actor.Kind == CreatureKind.Player)
            {
                _audio.PlayAt(SoundId.PlayerHurt, position, 0.9f);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An actor that can no longer give its anchor costs the sound, never the frame.
        }
    }

    private void OnActorDied(ActorDiedEvent evt)
    {
        try
        {
            if (evt.Actor.Id == _localActorId())
            {
                _audio.Play(SoundId.PlayerDeath);
                return;
            }

            Vector3 position = evt.Actor.GetWorldAnchor();
            _audio.PlayAt(evt.Actor.Kind == CreatureKind.Player ? SoundId.PlayerDeath : SoundId.WolfDeath, position);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}
