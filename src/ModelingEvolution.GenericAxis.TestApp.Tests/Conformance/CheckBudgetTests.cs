using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>GA-U-96 (ruling, § The checks): the CHK-13 and CHK-16 budgets are the protocol's.</summary>
public sealed class CheckBudgetTests
{
    [Theory]
    [InlineData(500_000, 10_000, 50_000, 24.6)]   // 490 mm at 50 mm/s: 2 × 9.8 s + 5 s
    [InlineData(10_000, 500_000, 50_000, 24.6)]   // direction does not matter
    [InlineData(10_000, 10_000, 50_000, 5.0)]     // already there: the fixed 5 s
    public void GA_U_96_Chk13Budget_IsTwiceTheTravelTimePlusFiveSeconds(long start, long target, long velocity, double seconds) =>
        CheckCatalog.Chk13Budget(start, target, velocity).TotalSeconds.Should().BeApproximately(seconds, 1e-9);

    [Fact]
    public void GA_U_96_Chk16MovingBudget_IsTwoSeconds() =>
        CheckCatalog.Chk16MovingBudget.Should().Be(TimeSpan.FromSeconds(2));
}
