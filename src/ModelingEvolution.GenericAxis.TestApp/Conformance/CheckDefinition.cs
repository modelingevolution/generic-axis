namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>One row of protocol.md § Conformance checks.</summary>
internal sealed record CheckDefinition(
    string Id,
    string Title,
    string Section,
    IReadOnlyList<string> Needs,
    bool RequiresMotion,
    Func<CheckContext, CancellationToken, Task<CheckOutcome>> RunAsync,
    IReadOnlyList<string> ObservedKeys)
{
    /// <summary>The exact <c>observed</c> keys of protocol § Observed values, in order, <c>retries</c> last.</summary>
    public IReadOnlyList<string> ReportedKeys => [.. ObservedKeys, "retries"];

    /// <summary>From CHK-06 onwards the checker holds the lease and beats, and every check restores the axis.</summary>
    public bool Restores => string.CompareOrdinal(Id, "CHK-06") >= 0;
}
