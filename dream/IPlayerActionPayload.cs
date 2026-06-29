using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(BuildUseIntent), "build.use")]
[JsonDerivedType(typeof(CombatUseIntent), "combat.use")]
[JsonDerivedType(typeof(PlayerStatsUpdateIntent), "player.stats.update")]
public interface IPlayerActionPayload
{
}
