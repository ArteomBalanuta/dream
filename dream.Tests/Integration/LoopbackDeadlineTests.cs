using Xunit;
using Xunit.Sdk;

namespace Dream.Tests.Integration;

// What LoopbackDeadline's progress budget promises, pinned directly, because a load test cannot
// pin it: the bug this covers (budgeting server ticks, which the waiting loop drives itself, for a
// condition observed on a client whose read loop had stalled) showed up once in a full suite and
// could not be reproduced on demand with a collection gate at 64 or with 24 CPU hogs. These tests
// reproduce it in milliseconds and would have failed on the old one-counter budget.
public class LoopbackDeadlineTests
{
    private const long Budget = 10;

    private static ProgressCounter Counter(string name, Func<long> read) => new(name, read);

    // The regression. One side races ahead - the ticks the loop spends itself - while the side the
    // condition is actually read from does nothing. That is a starved client, not a broken server,
    // so the budget must NOT be spent and the wait must stay open.
    [Fact]
    public void Budget_IsNotSpent_WhileAnySideHasMadeNoProgress()
    {
        long driven = 0;
        long starved = 0;
        ProgressCounter[] counters = [Counter("server ticks", () => driven), Counter("frames", () => starved)];
        LoopbackDeadline deadline = new(counters, Budget);

        for (int i = 0; i < Budget * 20; i++)
        {
            driven++;
            deadline.AssertAlive(counters, () => "the notice");
        }

        Assert.Equal(0, starved);
    }

    // Trickling counts: a side that is merely slow keeps the wait open for as long as it is still
    // getting somewhere, however far behind the other side runs.
    [Fact]
    public void Budget_IsNotSpent_WhileASlowSideIsStillTrickling()
    {
        long driven = 0;
        long trickling = 0;
        ProgressCounter[] counters = [Counter("server ticks", () => driven), Counter("frames", () => trickling)];
        LoopbackDeadline deadline = new(counters, Budget);

        // The fast side runs twenty times its budget; the slow side stays one short of its own.
        for (int i = 0; i < Budget * 20; i++)
        {
            driven++;
            if (i % 10 == 0 && trickling < Budget - 1)
            {
                trickling++;
            }

            deadline.AssertAlive(counters, () => "the notice");
        }

        Assert.Equal(Budget - 1, trickling);
        Assert.Equal(Budget * 20, driven);
    }

    // And the budget still does its job: once EVERY side has had its work and the condition has not
    // arrived, the wait fails instead of running on to the wall-clock cap.
    [Fact]
    public void Budget_IsSpent_OnceEverySideHasDoneTheWork()
    {
        long server = 0;
        long frames = 0;
        ProgressCounter[] counters = [Counter("server ticks", () => server), Counter("frames", () => frames)];
        LoopbackDeadline deadline = new(counters, Budget);

        XunitException failure = Assert.Throws<TrueException>(() =>
        {
            for (int i = 0; i < Budget * 20; i++)
            {
                server++;
                frames++;
                deadline.AssertAlive(counters, () => "the notice");
            }
        });

        // The message states what it measured, on every side, and claims nothing it did not measure.
        Assert.Contains("the notice", failure.Message, StringComparison.Ordinal);
        Assert.Contains("server ticks +", failure.Message, StringComparison.Ordinal);
        Assert.Contains("frames +", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not a loaded machine", failure.Message, StringComparison.Ordinal);
    }

    // With no budget the wait is bounded only by the no-progress window and the hard cap, which is
    // what the waits on a cadence rely on (a 120 s trade timeout is about 7680 ticks).
    [Fact]
    public void WithoutABudget_ProgressAloneNeverFailsTheWait()
    {
        long server = 0;
        ProgressCounter[] counters = [Counter("server ticks", () => server)];
        LoopbackDeadline deadline = new(counters);

        for (int i = 0; i < 100_000; i++)
        {
            server++;
            deadline.AssertAlive(counters, () => "a cadence");
        }
    }
}
