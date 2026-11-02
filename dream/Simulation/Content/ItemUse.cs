namespace Dream.Simulation.Content;

// What using (consuming) an item does. An item with a Use is consumed from the bag instead of equipped.
public enum ItemUseEffect : byte
{
    RestoreHealth,
    RestoreMana,
}

public sealed record ItemUse(ItemUseEffect Effect, int Amount);
