using System.Text;

namespace Dream.Client.Audio;

// Every one-shot the game can play. The clip files live in audio/sfx and are named after the id in
// snake_case, with an optional _1, _2, ... suffix for variants (Footstep -> footstep_1.wav).
public enum SoundId
{
    Footstep,
    Jump,
    Land,
    Swing,
    Hit,
    HitCritical,
    PlayerHurt,
    PlayerDeath,
    WolfHurt,
    WolfDeath,
    WolfGrowl,
    WolfHowl,
    BlockPlace,
    BlockBreak,
    BlockCrack,
    LootPickup,
    Equip,
    Unequip,
    PotionDrink,
    LevelUp,
    Respawn,
    StatSpend,
    UiClick,
    UiHover,
    InventoryOpen,
    InventoryClose
}

// Which volume slider a sound sits under (all of them also scale with Master).
public enum AudioBus
{
    Sfx,
    Music,
    Ui
}

public static class SoundIds
{
    public const int Count = (int)SoundId.InventoryClose + 1;

    public static AudioBus BusOf(SoundId id) => id switch
    {
        SoundId.StatSpend or SoundId.UiClick or SoundId.UiHover
            or SoundId.InventoryOpen or SoundId.InventoryClose => AudioBus.Ui,
        _ => AudioBus.Sfx
    };

    // Footstep -> "footstep", HitCritical -> "hit_critical".
    public static string FileStem(SoundId id)
    {
        string name = id.ToString();
        StringBuilder builder = new(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
