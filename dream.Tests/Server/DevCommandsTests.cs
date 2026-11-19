using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

// The /dev subcommand grammar: pure, no host, no gate. Who may run it is the host's decision (DevCommandHostTests).
public sealed class DevCommandsTests
{
    private static DevCommand Parse(string sub)
    {
        Assert.True(DevCommands.TryParse(sub, out DevCommand command, out string error), error);
        return command;
    }

    private static string Refused(string sub)
    {
        Assert.False(DevCommands.TryParse(sub, out _, out string error));
        Assert.NotEmpty(error);
        return error;
    }

    [Fact]
    public void Spawn_DefaultsToOneAtFourMetres_AndKeepsTheKeyAsTyped()
    {
        DevCommand command = Parse("spawn Wolf");

        Assert.Equal(DevCommandKind.Spawn, command.Kind);
        Assert.Equal("Wolf", command.Key);
        Assert.Equal(1, command.Count);
        Assert.Equal(4f, command.Distance);
    }

    [Theory]
    [InlineData("spawn wolf 16 16", 16, 16f)]
    [InlineData("SPAWN DireWolf 3 2.5", 3, 2.5f)]
    [InlineData("spawn wolf 1 1", 1, 1f)]
    [InlineData("spawn wolf 5", 5, 4f)]
    public void Spawn_AcceptsTheRanges_AnyCase(string sub, int count, float distance)
    {
        DevCommand command = Parse(sub);

        Assert.Equal(count, command.Count);
        Assert.Equal(distance, command.Distance);
    }

    [Theory]
    [InlineData("spawn")]
    [InlineData("spawn wolf 0")]
    [InlineData("spawn wolf 17")]
    [InlineData("spawn wolf -1")]
    [InlineData("spawn wolf x")]
    [InlineData("spawn wolf 2 0.9")]
    [InlineData("spawn wolf 2 16.1")]
    [InlineData("spawn wolf 2 NaN")]
    [InlineData("spawn wolf 2 4 9")]
    public void Spawn_OutsideTheRanges_IsRefusedWithUsage(string sub)
    {
        Assert.Contains("spawn", Refused(sub), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Teleport_Forms()
    {
        DevCommand xz = Parse("tp 12.5 -3");
        Assert.Equal((DevCommandKind.Teleport, "xz", 12.5f, -3f), (xz.Kind, xz.Key, xz.X, xz.Z));

        DevCommand camp = Parse("TP Camp north-east-wolves");
        Assert.Equal(("camp", "north-east-wolves"), (camp.Key, camp.Key2));

        DevCommand npc = Parse("tp npc Vendor");
        Assert.Equal(("npc", "Vendor"), (npc.Key, npc.Key2));

        Assert.Equal("spawn", Parse("tp SPAWN").Key);
    }

    [Theory]
    [InlineData("tp")]
    [InlineData("tp 5")]
    [InlineData("tp a b")]
    [InlineData("tp 1 2 3")]
    [InlineData("tp camp")]
    [InlineData("tp npc")]
    [InlineData("tp spawn now")]
    [InlineData("tp Infinity 5")]
    [InlineData("tp 1e9 5")]
    [InlineData("tp 1,5 2")]
    public void Teleport_Malformed_IsRefused(string sub)
    {
        Assert.Contains("tp", Refused(sub), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("give tool.torch", null, 1)]
    [InlineData("give gear.iron-dagger rare", "rare", 1)]
    [InlineData("give gear.iron-dagger Uncommon 20", "uncommon", 20)]
    [InlineData("give gear.iron-dagger 3 common", "common", 3)]
    [InlineData("give tool.torch 7", null, 7)]
    public void Give_Forms(string sub, string? rarity, int count)
    {
        DevCommand command = Parse(sub);

        Assert.Equal(DevCommandKind.Give, command.Kind);
        Assert.Equal(rarity, command.Rarity);
        Assert.Equal(count, command.Count);
    }

    [Theory]
    [InlineData("give")]
    [InlineData("give x 0")]
    [InlineData("give x 21")]
    [InlineData("give x legendary")]
    [InlineData("give x rare rare")]
    [InlineData("give x 2 3")]
    [InlineData("give x rare 2 9")]
    public void Give_Malformed_IsRefused(string sub)
    {
        Assert.Contains("give", Refused(sub), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("clear", 0f)]
    [InlineData("CLEAR 6.5", 6.5f)]
    [InlineData("clear 1000", 1000f)]
    public void Clear_RadiusIsOptional(string sub, float radius)
    {
        DevCommand command = Parse(sub);

        Assert.Equal(DevCommandKind.Clear, command.Kind);
        Assert.Equal(radius, command.Distance);
    }

    [Theory]
    [InlineData("clear 0")]
    [InlineData("clear -2")]
    [InlineData("clear 1000.5")]
    [InlineData("clear a")]
    [InlineData("clear 1 2")]
    public void Clear_BadRadius_IsRefused(string sub)
    {
        Assert.Contains("clear", Refused(sub), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealHpMana_AndHelp()
    {
        Assert.Equal(DevCommandKind.Heal, Parse("HEAL").Kind);
        Assert.Equal(DevCommandKind.Help, Parse("help").Kind);
        DevCommand hp = Parse("hp 40");
        Assert.Equal((DevCommandKind.SetHealth, 40), (hp.Kind, hp.Count));
        DevCommand mana = Parse("Mana 0");
        Assert.Equal((DevCommandKind.SetMana, 0), (mana.Kind, mana.Count));
        Assert.Equal(-5, Parse("hp -5").Count); // not refused here: the runtime clamps to 1..max

        Refused("heal now");
        Refused("hp");
        Refused("hp a");
        Refused("mana 1 2");
        Refused("hp 99999999999");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("frobnicate")]
    [InlineData("godmode")]
    [InlineData("time 12")]
    public void Unknown_OrEmpty_IsRefusedWithTheUsage(string sub)
    {
        Assert.Contains("spawn", Refused(sub), StringComparison.Ordinal);
    }
}
