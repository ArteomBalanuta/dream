using Dream.Rendering.Effects;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

public class FxParticlePoolTests
{
    private static FxParticle Particle(float marker)
    {
        return new FxParticle { Life = 1f, Position = new Vector3(marker, 0f, 0f) };
    }

    [Fact]
    public void Add_Stores_Particles_In_Order()
    {
        FxParticlePool pool = new(4);

        Assert.True(pool.TryAdd(Particle(1f)));
        Assert.True(pool.TryAdd(Particle(2f)));

        Assert.Equal(2, pool.Count);
        Assert.Equal(1f, pool[0].Position.X);
        Assert.Equal(2f, pool[1].Position.X);
    }

    [Fact]
    public void RemoveAt_Swaps_The_Last_Particle_Into_The_Hole()
    {
        FxParticlePool pool = new(4);
        for (int i = 1; i <= 3; i++)
        {
            pool.TryAdd(Particle(i));
        }

        pool.RemoveAt(0);

        Assert.Equal(2, pool.Count);
        Assert.Equal(3f, pool[0].Position.X);
        Assert.Equal(2f, pool[1].Position.X);
    }

    [Fact]
    public void Removing_The_Last_Particle_Just_Shrinks_The_Pool()
    {
        FxParticlePool pool = new(2);
        pool.TryAdd(Particle(1f));
        pool.TryAdd(Particle(2f));

        pool.RemoveAt(1);

        Assert.Equal(1, pool.Count);
        Assert.Equal(1f, pool[0].Position.X);
    }

    [Fact]
    public void RemoveAt_Rejects_An_Index_Outside_The_Live_Range()
    {
        FxParticlePool pool = new(2);
        pool.TryAdd(Particle(1f));

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.RemoveAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.RemoveAt(-1));
    }

    [Fact]
    public void A_Full_Pool_Drops_The_Spawn_And_Counts_It()
    {
        FxParticlePool pool = new(2);
        Assert.True(pool.TryAdd(Particle(1f)));
        Assert.True(pool.TryAdd(Particle(2f)));

        Assert.False(pool.TryAdd(Particle(3f)));
        Assert.False(pool.TryAdd(Particle(4f)));

        Assert.Equal(2, pool.Count);
        Assert.Equal(2L, pool.DroppedSpawns);
        Assert.Equal(2f, pool[1].Position.X);
    }

    [Fact]
    public void A_Quiet_Add_To_A_Full_Pool_Is_Not_Counted()
    {
        FxParticlePool pool = new(1);
        pool.TryAdd(Particle(1f));

        Assert.False(pool.TryAddQuietly(Particle(2f)));

        Assert.Equal(0L, pool.DroppedSpawns);
    }

    [Fact]
    public void A_Freed_Slot_Can_Be_Reused()
    {
        FxParticlePool pool = new(1);
        pool.TryAdd(Particle(1f));
        pool.RemoveAt(0);

        Assert.True(pool.TryAdd(Particle(2f)));
        Assert.Equal(2f, pool[0].Position.X);
        Assert.Equal(0L, pool.DroppedSpawns);
    }
}
