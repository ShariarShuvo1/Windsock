using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Calling off a fight with Explorer over the task band: more than five
/// narrowings in ten seconds and the band is left alone.
/// </summary>
public sealed class RetryBudgetTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Now_AndThen_IsFine()
    {
        RetryBudget budget = new(5, At(10));

        for (int each = 0; each < 20; each++)
        {
            Assert.True(budget.Try(At(each * 3)));
        }

        Assert.False(budget.IsSpent);
    }

    [Fact]
    public void EveryPatrol_IsAFightAndIsCalledOff()
    {
        RetryBudget budget = new(5, At(10));

        for (int each = 0; each < 5; each++)
        {
            Assert.True(budget.Try(At(each * 0.3)));
        }

        Assert.False(budget.Try(At(1.5)));
        Assert.True(budget.IsSpent);
        Assert.False(budget.Try(At(100)));
    }

    [Fact]
    public void Resetting_StartsAfresh()
    {
        RetryBudget budget = new(1, At(10));

        budget.Try(At(0));
        budget.Try(At(1));
        budget.Reset();

        Assert.False(budget.IsSpent);
        Assert.True(budget.Try(At(2)));
    }
}
