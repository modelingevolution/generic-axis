using ModelingEvolution.Drawing.Units;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// A linear axis over the generic map (ADR-6): converts <c>Length</c> / <c>Speed</c> / <c>Percentage</c> to
/// millimetres and delegates to the unit-free <see cref="AxisEngine"/>.
/// </summary>
public sealed class ModbusLinearAxis : ILinearAxis
{
    private readonly AxisEngine _engine;

    internal ModbusLinearAxis(AxisEngine engine)
    {
        _engine = engine;
        _engine.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
    }

    /// <inheritdoc/>
    public string Name => _engine.Name;

    /// <inheritdoc/>
    public string DisplayName => _engine.Options.EffectiveDisplayName;

    /// <inheritdoc/>
    public AxisState State => _engine.State;

    /// <inheritdoc/>
    public AxisCapabilities Capabilities => AxisCapabilities.Homing;

    /// <inheritdoc/>
    public AxisStatus Status => _engine.Status;

    /// <summary>The effective machine limits and where they came from.</summary>
    public AxisLimits Limits => _engine.Limits;

    /// <inheritdoc/>
    public event EventHandler<AxisStatus>? StatusChanged;

    /// <inheritdoc/>
    public Length<double, Millimetre<double>>? Offset =>
        _engine.Status.Position is { } p ? new Length<double, Millimetre<double>>(p) : null;

    /// <inheritdoc/>
    public Length<double, Millimetre<double>> Min => new(_engine.Limits.TravelMin);

    /// <inheritdoc/>
    public Length<double, Millimetre<double>> Max => new(_engine.Limits.TravelMax);

    /// <inheritdoc/>
    public Length<double, Millimetre<double>> Tolerance => new(_engine.Options.Tolerance);

    /// <inheritdoc/>
    public Speed<double, MillimetrePerSecond<double>> MinSpeed => new(Words.Quantum);

    /// <inheritdoc/>
    public Speed<double, MillimetrePerSecond<double>> MaxSpeed => new(_engine.Limits.MaxVelocity);

    /// <inheritdoc/>
    /// <remarks>The tick is the status cadence (ADR-15); this returns the last tick's status.</remarks>
    public Task<AxisStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_engine.Status);
    }

    /// <inheritdoc/>
    public Task PowerAsync(bool on, CancellationToken ct = default) => _engine.PowerAsync(on, ct);

    /// <inheritdoc/>
    public Task HomeAsync(CancellationToken ct = default) => _engine.HomeAsync(ct);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default) => _engine.StopAsync(ct);

    /// <inheritdoc/>
    public Task ResetAsync(CancellationToken ct = default) => _engine.ResetAsync(ct);

    /// <inheritdoc/>
    public Task MoveAbsoluteAsync(Length<double, Millimetre<double>> target,
        Speed<double, MillimetrePerSecond<double>>? speed = null, CancellationToken ct = default) =>
        _engine.MoveAbsoluteAsync(target.Value, SpeedRequest.Of(speed?.Value), null, ct);

    /// <inheritdoc/>
    public Task MoveAbsoluteAsync(Length<double, Millimetre<double>> target, Percentage speedOfMax,
        CancellationToken ct = default) =>
        _engine.MoveAbsoluteAsync(target.Value, SpeedRequest.Of(speedOfMax), null, ct);

    /// <inheritdoc/>
    public Task MoveRelativeAsync(Length<double, Millimetre<double>> delta,
        Speed<double, MillimetrePerSecond<double>>? speed = null, CancellationToken ct = default) =>
        _engine.MoveRelativeAsync(delta.Value, SpeedRequest.Of(speed?.Value), ct);

    /// <inheritdoc/>
    public Task MoveRelativeAsync(Length<double, Millimetre<double>> delta, Percentage speedOfMax,
        CancellationToken ct = default) =>
        _engine.MoveRelativeAsync(delta.Value, SpeedRequest.Of(speedOfMax), ct);

    /// <inheritdoc/>
    public Task MoveVelocityAsync(Speed<double, MillimetrePerSecond<double>> velocity, CancellationToken ct = default) =>
        _engine.MoveVelocityAsync(velocity.Value, ct);
}
