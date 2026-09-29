using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-U-100 (review #31, protocol § Rules "Timing"): a trip happened between the last read without it and the first read
/// with it; it is early only if that first read is before 1.0 s, late only if that last read is already after 1.5 s.
/// </summary>
public sealed class TripWindowTests
{
    [Theory]
    [InlineData(1494, 1514)] // a trip at ~1490-1500 ms first SEEN at 1514 ms: conforming (the showing read alone would FAIL it)
    [InlineData(1480, 1500)]
    [InlineData(985, 1005)]  // first seen at 1005 ms: inside
    [InlineData(0, 1000)]    // the first read already showed it, at 1.0 s
    public void GA_U_100_ATripBracketedByTheWindowPasses(long before, long after) =>
        CheckCatalog.TripWindow(before, after).Should().BeNull();

    [Fact]
    public void GA_U_100_LateOnlyWhenTheLastReadWithoutItIsAlreadyAfter1Point5s() =>
        CheckCatalog.TripWindow(1510, 1530).Should().Be("tripped between 1510 and 1530 ms after the last beat, after the 1.5 s bound");

    [Fact]
    public void GA_U_100_EarlyOnlyWhenTheFirstReadWithItIsBefore1s() =>
        CheckCatalog.TripWindow(970, 990).Should().Be("tripped between 970 and 990 ms after the last beat, before the 1 s stall window");
}
