using Xunit.Abstractions;
using Xunit.Sdk;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>
/// Fixture-cadence INCONCLUSIVE (adr.md, lead ruling "Two-vCPU flakes"). A timing budget is asserted unchanged. Only
/// when it fails AND <see cref="MiniPlc"/> missed its own 10 ms scan by more than <see cref="MissedAbove"/> during
/// the measured window is the result INCONCLUSIVE (reported as a skip naming the gap): the in-process fixture cannot
/// measure a budget it did not keep itself. A budget failure with the fixture on cadence stays RED. Nothing is
/// skipped before the budget was measured.
/// </summary>
internal static class Cadence
{
    /// <summary>A scan gap above this means the fixture missed its cadence (nominal 10 ms).</summary>
    public static readonly TimeSpan MissedAbove = TimeSpan.FromMilliseconds(50);

    /// <summary>Asserts a budget measured in a window whose fixture max scan gap was <paramref name="maxScanGap"/>.</summary>
    public static void Budget(TimeSpan maxScanGap, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (Exception failure) when (failure is not InconclusiveException && maxScanGap > MissedAbove)
        {
            throw Inconclusive(maxScanGap, failure);
        }
    }

    /// <summary>
    /// Awaits a step bounded by a budget (a move that a false fixture trip would fail, a bounded wait); on failure the
    /// fixture's gap is read at that moment, the end of the window.
    /// </summary>
    public static async Task Budget(MiniPlc plc, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception failure) when (failure is not InconclusiveException && plc.MaxScanGap is var gap && gap > MissedAbove)
        {
            throw Inconclusive(gap, failure);
        }
    }

    private static InconclusiveException Inconclusive(TimeSpan gap, Exception failure) =>
        new($"INCONCLUSIVE: fixture missed cadence: max scan gap {gap.TotalMilliseconds:F0} ms "
            + $"(> {MissedAbove.TotalMilliseconds:F0} ms). Budget failure: {failure.Message}", failure);
}

/// <summary>The fixture did not keep its own cadence; <see cref="TimingFactAttribute"/> reports it as skipped.</summary>
internal sealed class InconclusiveException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// A <see cref="FactAttribute"/> whose <see cref="InconclusiveException"/> is reported as a skip (xunit 2.9.3 has no
/// dynamic skip). Any other failure is reported as it is.
/// </summary>
[XunitTestCaseDiscoverer("ModelingEvolution.GenericAxis.Tests.Support.TimingFactDiscoverer", "ModelingEvolution.GenericAxis.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class TimingFactAttribute : FactAttribute;

public sealed class TimingFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo factAttribute)
    {
        yield return new TimingTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

public sealed class TimingTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public TimingTestCase()
    {
    }

    public TimingTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay display, TestMethodDisplayOptions options,
        ITestMethod testMethod)
        : base(diagnosticMessageSink, display, options, testMethod)
    {
    }

    public override async Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
        object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        using var bus = new InconclusiveBus(messageBus);
        var summary = await base.RunAsync(diagnosticMessageSink, bus, constructorArguments, aggregator,
            cancellationTokenSource);
        summary.Failed -= bus.Skipped;
        summary.Skipped += bus.Skipped;
        return summary;
    }

    /// <summary>Turns a test failure whose outermost exception is <see cref="InconclusiveException"/> into a skip.</summary>
    internal sealed class InconclusiveBus(IMessageBus inner) : IMessageBus
    {
        public int Skipped { get; private set; }

        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestFailed failed && failed.ExceptionTypes.FirstOrDefault() == typeof(InconclusiveException).FullName)
            {
                Skipped++;
                return inner.QueueMessage(new TestSkipped(failed.Test, failed.Messages[0]));
            }

            return inner.QueueMessage(message);
        }

        // The inner bus belongs to the runner.
        public void Dispose()
        {
        }
    }
}
