namespace Dream.Rendering.Ui;

// Widget ids that carry the identity the widget was drawn from.
//
// UiInputRouter acts on RELEASE and requires _pressId == id && _hot == id, so the only thing a press
// captures is the integer id. A control whose id is its POSITION in a live list (row n, label n) can
// therefore be redrawn under the pointer with a different payload between the press and the release,
// and the release then acts on the new occupant: the click is redirected, not dropped.
//
// The cure, already used by NpcDialogueWindow.ActionId, is to fold what the widget acts on into its
// id. Anything that changes mid-press makes the ids disagree and the router drops the click, which is
// the safe failure mode: a press on one item can never release as another.
//
// The identity does not fit in an int, so it is hashed into a band of its own, 0x5000_0000..
// 0x5FFF_FFFF, which no other HUD id uses (the ordinary ids are below 0x0100_0000 and the quest
// action button has 0x4000_0000..0x4FFF_FFFF). A stable FNV-1a is used, not string.GetHashCode or
// object hashes: those are randomised per process, and the id must be the same in the press frame and
// the release frame of the same run. Pure arithmetic, so a per-frame caller allocates nothing.
public static class HudWidgetId
{
    internal const int IdentityBand = 0x5000_0000;
    internal const int IdentityMask = 0x0FFF_FFFF;

    // seed keeps two controls that happen to act on the same thing apart (a trade row and a sell row
    // for one item); identity is what the widget acts on (an item instance id, a ground item id, an
    // actor id).
    public static int Of(int seed, long identity)
    {
        uint hash = 2166136261u;
        hash = Fold(hash, (uint)seed);
        hash = Fold(hash, (uint)identity);
        hash = Fold(hash, (uint)(identity >> 32));
        return IdentityBand + (int)(hash & IdentityMask);
    }

    private static uint Fold(uint hash, uint value)
    {
        for (int b = 0; b < 4; b++)
        {
            hash = (hash ^ ((value >> (b * 8)) & 0xFF)) * 16777619u;
        }

        return hash;
    }
}
