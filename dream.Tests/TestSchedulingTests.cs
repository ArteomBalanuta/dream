using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Dream.Tests;

// Guards TestScheduling's class lists: a renamed or deleted class would silently fall out of them.
public class TestSchedulingTests
{
    public static TheoryData<string> ScheduledClasses()
    {
        TheoryData<string> data = new();
        foreach (string name in TestScheduling.SplitClasses.Concat(TestScheduling.RealTimeClasses).Concat(TestScheduling.LongestFirst).Distinct())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScheduledClasses))]
    public void Every_Scheduled_Class_Exists_And_Has_Tests(string className)
    {
        Type? type = typeof(TestSchedulingTests).Assembly.GetType(className);

        Assert.NotNull(type);
        Assert.Contains(
            type!.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any());
    }

    // A class in a named collection shares it with other classes (TimingSensitive, RenderThreadAffinity,
    // a fixture), so splitting it would break what the collection guarantees; Split skips such
    // classes, and this keeps the list honest.
    [Fact]
    public void Split_Classes_Are_Not_In_A_Named_Collection()
    {
        foreach (string className in TestScheduling.SplitClasses)
        {
            Type type = typeof(TestSchedulingTests).Assembly.GetType(className)!;
            Assert.Null(type.GetCustomAttribute<CollectionAttribute>());
        }
    }

    [Fact]
    public void RealTime_Classes_Are_Split()
    {
        Assert.Subset((ISet<string>)TestScheduling.SplitClasses, (ISet<string>)TestScheduling.RealTimeClasses);
    }

    [Fact]
    public void LongestFirst_Lists_Each_Class_Once()
    {
        Assert.Equal(TestScheduling.LongestFirst.Count, TestScheduling.LongestFirst.Distinct(StringComparer.Ordinal).Count());
    }

    // ClassOf reads the class from xUnit's default collection display name ("Test collection for
    // <class>"). If an upgrade renamed it, ClassOf would return null for every collection and the
    // real-time and longest-first scheduling would silently stop applying; discovering real test
    // cases with xUnit's own discoverer catches that.
    [Fact]
    public void ClassOf_Returns_The_Class_Of_A_Discovered_Default_Collection()
    {
        IReadOnlyList<IXunitTestCase> cases = Discover(typeof(TestSchedulingTests));

        Assert.NotEmpty(cases);
        foreach (IXunitTestCase testCase in cases)
        {
            Assert.Equal(typeof(TestSchedulingTests).FullName, TestScheduling.ClassOf(testCase.TestMethod.TestClass.TestCollection));
        }
    }

    // The same through Split: each case of a split class gets its own collection, still named for
    // its class, so RealTimeClasses and LongestFirst keep applying to it.
    [Fact]
    public void Split_Moves_Each_Discovered_Case_To_Its_Own_Collection_That_ClassOf_Still_Maps()
    {
        Type splitClass = typeof(TestSchedulingTests).Assembly.GetType(TestScheduling.RealTimeClasses.First())!;
        IReadOnlyList<IXunitTestCase> cases = Discover(splitClass);
        Assert.True(cases.Count >= 2, $"{splitClass.Name} has {cases.Count} test cases.");
        Assert.Single(cases.Select(testCase => testCase.TestMethod.TestClass.TestCollection.UniqueID).Distinct());
        List<string> diagnostics = [];

        Assert.True(TestScheduling.Split(cases, diagnostics.Add));

        Assert.Empty(diagnostics);
        Assert.Equal(cases.Count, cases.Select(testCase => testCase.TestMethod.TestClass.TestCollection.UniqueID).Distinct().Count());
        foreach (IXunitTestCase testCase in cases)
        {
            Assert.Equal(splitClass.FullName, TestScheduling.ClassOf(testCase.TestMethod.TestClass.TestCollection));
            Assert.Equal(splitClass.FullName, testCase.TestMethod.TestClass.Class.Name);
        }
    }

    [Theory]
    [InlineData(null, 16, 12)]
    [InlineData("", 16, 12)]
    [InlineData("abc", 16, 12)]
    [InlineData("0", 16, 12)]
    [InlineData("-3", 16, 12)]
    [InlineData("8", 16, 8)]
    [InlineData("64", 16, 64)]
    [InlineData("65", 16, 64)]
    [InlineData("100000", 16, 64)]
    [InlineData(null, 2, 4)]
    [InlineData("9", 2, 8)]
    [InlineData(null, 1, 4)]
    public void MaxRunningCollections_Is_Clamped_To_The_Pool_Minimum(string? configured, int processorCount, int expected)
    {
        int resolved = TestScheduling.ResolveMaxRunningCollections(configured, processorCount);

        Assert.Equal(expected, resolved);
        Assert.InRange(resolved, 1, processorCount * TestAssemblyInitializer.ThreadsPerCore);
    }

    [Fact]
    public void The_Running_Gate_Stays_Within_The_Pool_Minimum()
    {
        Assert.InRange(TestScheduling.MaxRunningCollections, 1, Environment.ProcessorCount * TestAssemblyInitializer.ThreadsPerCore);
    }

    // xUnit's own discovery, as the runner does it: the default (collection per class) factory
    // names the collections.
    private static IReadOnlyList<IXunitTestCase> Discover(Type testClass)
    {
        using DiscoverySink sink = new();
        using XunitTestFrameworkDiscoverer discoverer = new(
            Reflector.Wrap(testClass.Assembly),
            new NoSourceInformation(),
            sink);
        discoverer.Find(testClass.FullName!, includeSourceInformation: false, sink, new DiscoveryOptions());
        Assert.True(sink.Finished.Wait(TimeSpan.FromSeconds(30)), "Discovery did not finish.");
        return sink.TestCases;
    }

    private sealed class DiscoverySink : LongLivedMarshalByRefObject, IMessageSink, IDisposable
    {
        private readonly List<IXunitTestCase> _testCases = [];

        public ManualResetEventSlim Finished { get; } = new();

        public IReadOnlyList<IXunitTestCase> TestCases
        {
            get
            {
                lock (_testCases)
                {
                    return _testCases.ToList();
                }
            }
        }

        public bool OnMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestCaseDiscoveryMessage discovered when discovered.TestCase is IXunitTestCase testCase:
                    lock (_testCases)
                    {
                        _testCases.Add(testCase);
                    }

                    break;
                case IDiscoveryCompleteMessage:
                    Finished.Set();
                    break;
            }

            return true;
        }

        public void Dispose() => Finished.Dispose();
    }

    private sealed class NoSourceInformation : LongLivedMarshalByRefObject, ISourceInformationProvider
    {
        public ISourceInformation? GetSourceInformation(ITestCase testCase) => null;

        public void Dispose()
        {
        }
    }

    private sealed class DiscoveryOptions : ITestFrameworkDiscoveryOptions
    {
        private readonly Dictionary<string, object?> _values = [];

        public TValue GetValue<TValue>(string name) =>
            _values.TryGetValue(name, out object? value) && value is TValue typed ? typed : default!;

        public void SetValue<TValue>(string name, TValue value) => _values[name] = value;
    }
}
