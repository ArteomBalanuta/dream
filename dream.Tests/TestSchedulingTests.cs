using System.Reflection;
using Xunit;

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
}
