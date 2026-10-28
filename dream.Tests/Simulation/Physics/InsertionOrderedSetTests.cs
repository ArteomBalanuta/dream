using System.Diagnostics;
using Dream.Simulation.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.Physics;

// InsertionOrderedSet replaces List<T> for the physics body lists and Map's solid cubes, whose
// iteration order feeds the simulation (sync order, Bullet removal order, checksum order). It must
// iterate and index exactly like the List it replaced under any mix of Add and Remove.
public class InsertionOrderedSetTests
{
    private readonly ITestOutputHelper _output;

    public InsertionOrderedSetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class Item(int id)
    {
        public int Id { get; } = id;

        public override string ToString() => Id.ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomAddRemove_IteratesAndIndexesExactlyLikeList(int seed)
    {
        Random random = new(seed);
        List<Item> reference = [];
        InsertionOrderedSet<Item> set = new();
        int nextId = 0;
        for (int op = 0; op < 20_000; op++)
        {
            int roll = random.Next(100);
            if (roll < 55 || reference.Count == 0)
            {
                Item item = new(nextId++);
                reference.Add(item);
                set.Add(item);
            }
            else if (roll < 95)
            {
                Item victim = reference[random.Next(reference.Count)];
                Assert.True(reference.Remove(victim));
                Assert.True(set.Remove(victim));
                Assert.False(set.Remove(victim));
            }
            else if (roll < 97)
            {
                // Indexed reads compact; they must not reorder.
                int index = random.Next(reference.Count);
                Assert.Same(reference[index], set[index]);
            }
            else if (roll < 98)
            {
                reference.Clear();
                set.Clear();
            }
            else
            {
                Assert.Equal(reference, set.ToList());
            }

            Assert.Equal(reference.Count, set.Count);
        }

        Assert.Equal(reference, set.ToList());
        for (int i = 0; i < reference.Count; i++)
        {
            Assert.Same(reference[i], set[i]);
        }

        foreach (Item item in reference)
        {
            Assert.True(set.Contains(item));
        }
    }

    [Fact]
    public void Sequences_IncreaseAndIdentifyItemsAddedSinceAMark()
    {
        InsertionOrderedSet<Item> set = new();
        Item first = new(1);
        Item second = new(2);
        set.Add(first);
        set.Add(second);
        long mark = set.NextSequence;
        Item third = new(3);
        set.Add(third);
        set.Remove(second);

        List<Item> addedSinceMark = [];
        for (int slot = 0; slot < set.SlotCount; slot++)
        {
            Item? item = set.SlotAt(slot, out long sequence);
            if (item != null && sequence >= mark)
            {
                addedSinceMark.Add(item);
            }
        }

        Assert.Equal([third], addedSinceMark);
        Assert.Equal(1, set.RemovedCount);
        Assert.False(set.TryAdd(first));
        Assert.Throws<ArgumentException>(() => set.Add(first));
    }

    // A List<T>.Remove from the middle of 100k items scans and shifts; the set's Remove is a
    // dictionary lookup plus a tombstone. Removing every item in random order is O(N) in total for
    // the set and O(N^2) (about 5e9 element moves here) for a List. The bound is generous.
    [Fact]
    public void RemovingEveryItemInRandomOrder_IsLinearOverall()
    {
        const int count = 100_000;
        Item[] items = Enumerable.Range(0, count).Select(i => new Item(i)).ToArray();
        InsertionOrderedSet<Item> set = new();
        foreach (Item item in items)
        {
            set.Add(item);
        }

        Random random = new(7);
        Item[] order = items.OrderBy(_ => random.Next()).ToArray();
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < order.Length; i++)
        {
            set.Remove(order[i]);
            if ((i & 1023) == 0 && set.Count > 0)
            {
                _ = set[0];
            }
        }

        stopwatch.Stop();
        _output.WriteLine($"removed {count} items in {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.Empty(set);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Enumeration_DoesNotAllocate()
    {
        InsertionOrderedSet<Item> set = new();
        Item[] items = Enumerable.Range(0, 64).Select(i => new Item(i)).ToArray();
        foreach (Item item in items)
        {
            set.Add(item);
        }

        set.Remove(items[10]);
        set.Remove(items[20]);
        int sum = 0;
        long bytes = Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            foreach (Item item in set)
            {
                sum += item.Id;
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(sum > 0);
    }

    // Like List<T>: a mutation during enumeration fails loudly instead of silently skipping or
    // repeating items. Compaction counts, because it moves items under the enumerator.
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("remove-last")]
    [InlineData("clear")]
    [InlineData("compacting-index")]
    public void MutationDuringEnumeration_Throws(string mutation)
    {
        InsertionOrderedSet<Item> set = new();
        Item[] items = Enumerable.Range(0, 8).Select(i => new Item(i)).ToArray();
        foreach (Item item in items)
        {
            set.Add(item);
        }

        // Leaves a tombstone, so the next indexed read compacts.
        set.Remove(items[2]);

        InsertionOrderedSet<Item>.Enumerator enumerator = set.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        switch (mutation)
        {
            case "add":
                set.Add(new Item(100));
                break;
            case "remove":
                set.Remove(items[5]);
                break;
            case "remove-last":
                set.Remove(items[7]);
                break;
            case "clear":
                set.Clear();
                break;
            case "compacting-index":
                _ = set[3];
                break;
        }

        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        Assert.Throws<InvalidOperationException>(() => enumerator.Reset());
    }

    [Fact]
    public void NonMutatingCallsDuringEnumeration_DoNotThrow()
    {
        InsertionOrderedSet<Item> set = new();
        Item[] items = Enumerable.Range(0, 8).Select(i => new Item(i)).ToArray();
        foreach (Item item in items)
        {
            set.Add(item);
        }

        List<int> seen = [];
        foreach (Item item in set)
        {
            seen.Add(item.Id);
            Assert.True(set.Contains(item));
            Assert.False(set.TryAdd(item));
            Assert.False(set.Remove(new Item(-1)));
            _ = set[0];
        }

        Assert.Equal(Enumerable.Range(0, 8), seen);
    }
}
