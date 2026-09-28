using ModelingEvolution.Drawing;
using ModelingEvolution.Drawing.Units;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// A rotary axis over the generic map (ADR-6): converts <c>Degree</c> / <c>AngularSpeed</c> / <c>Percentage</c> to
/// degrees and delegates to the unit-free <see cref="AxisEngine"/>. Map version 1 has limited rotary travel with no
/// wrap, so any <see cref="RotationSense"/> other than <see cref="RotationSense.Shortest"/> is refused (G9).
/// </summary>
public sealed class ModbusRotaryAxis : IRotaryAxis
{
    private readonly AxisEngine _engine;

    internal ModbusRotaryAxis(AxisEngine engine)
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
    public Degree<double>? Angle => _engine.Status.Position is { } p ? Degree<double>.Create(p) : null;

    /// <inheritdoc/>
    public Degree<double> Min => Degree<double>.Create(_engine.Limits.TravelMin);

    /// <inheritdoc/>
    public Degree<double> Max => Degree<double>.Create(_engine.Limits.TravelMax);

    /// <inheritdoc/>
    public Degree<double> Tolerance => Degree<double>.Create(_engine.Options.Tolerance);

    /// <inheritdoc/>
    public AngularSpeed<double, DegreePerSecond<double>> MinSpeed => new(Words.Quantum);

    /// <inheritdoc/>
    public AngularSpeed<double, DegreePerSecond<double>> MaxSpeed => new(_engine.Limits.MaxVelocity);

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
    public Task MoveAbsoluteAsync(Degree<double> target, AngularSpeed<double, DegreePerSecond<double>>? speed = null,
        RotationSense sense = RotationSense.Shortest, CancellationToken ct = default) =>
        _engine.MoveAbsoluteAsync((double)target, SpeedRequest.Of(speed?.Value), sense, ct);

    /// <inheritdoc/>
    public Task MoveAbsoluteAsync(Degree<double> target, Percentage speedOfMax,
        RotationSense sense = RotationSense.Shortest, CancellationToken ct = default) =>
        _engine.MoveAbsoluteAsync((double)target, SpeedRequest.Of(speedOfMax), sense, ct);

    /// <inheritdoc/>
    public Task MoveRelativeAsync(Degree<double> delta, AngularSpeed<double, DegreePerSecond<double>>? speed = null,
        CancellationToken ct = default) =>
        _engine.MoveRelativeAsync((double)delta, SpeedRequest.Of(speed?.Value), ct);

    /// <inheritdoc/>
    public Task MoveRelativeAsync(Degree<double> delta, Percentage speedOfMax, CancellationToken ct = default) =>
        _engine.MoveRelativeAsync((double)delta, SpeedRequest.Of(speedOfMax), ct);

    /// <inheritdoc/>
    public Task MoveVelocityAsync(AngularSpeed<double, DegreePerSecond<double>> velocity,
        CancellationToken ct = default) =>
        _engine.MoveVelocityAsync(velocity.Value, ct);
}
