namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The FR-11 advisory lease rule (protocol § FR-11 "Advisory lease"), the pure half of lease acquisition. Mirrors
/// delta-positioner's <c>AdvisoryLease</c> with the register renamed to <c>LeaseOwner</c>.
///
/// <para>
/// <b>Advisory, stated plainly.</b> Modbus has no compare-and-swap, so two drivers starting in the same window can
/// both pass; the PLC watchdog bounds the consequence.
/// </para>
/// </summary>
public static class AdvisoryLease
{
    /// <summary><c>LeaseOwner</c> value meaning nobody holds the axis.</summary>
    public const ushort Unowned = 0;

    /// <summary>Lease expiry: the watchdog's stall window (protocol: 1 s).</summary>
    public static TimeSpan Expiry => RegisterMap.WatchdogWindow;

    /// <summary>
    /// Decides whether this driver may attach. <b>Grant if <c>LeaseOwner</c> is unowned, or is already ours, or the
    /// incumbent's heartbeat has been unchanged for at least <paramref name="expiry"/>.</b> The comparison is
    /// <c>&gt;=</c>: a beat exactly one window old has already expired.
    /// </summary>
    /// <param name="owner">The value read from <c>LeaseOwner</c>.</param>
    /// <param name="heartbeatAge">How long <c>Heartbeat</c> has been observed unchanged.</param>
    /// <param name="expiry">The expiry window (<see cref="Expiry"/>).</param>
    /// <param name="myOwnerId">This driver's station-unique non-zero id.</param>
    /// <returns>The decision and why; the reason names register <c>LeaseOwner</c> and the owner seen.</returns>
    public static LeaseDecision Evaluate(ushort owner, TimeSpan heartbeatAge, TimeSpan expiry, ushort myOwnerId)
    {
        if (owner == Unowned)
            return new LeaseDecision(true, owner, "LeaseOwner is 0 — unowned");

        if (owner == myOwnerId)
            return new LeaseDecision(true, owner, $"LeaseOwner is {owner} — already ours, reattaching");

        if (heartbeatAge >= expiry)
            return new LeaseDecision(true, owner,
                $"LeaseOwner is {owner} but its Heartbeat has been unchanged for {heartbeatAge.TotalSeconds:F2} s "
                + $"(expiry {expiry.TotalSeconds:F2} s) — the lease has expired");

        return new LeaseDecision(false, owner,
            $"LeaseOwner is {owner} and its Heartbeat changed {heartbeatAge.TotalSeconds:F2} s ago — "
            + "another commander holds the axis");
    }
}

/// <summary>Outcome of a lease check.</summary>
/// <param name="Granted">The driver may attach.</param>
/// <param name="Owner">The <c>LeaseOwner</c> value the decision was made on.</param>
/// <param name="Reason">Why, worth logging on both outcomes.</param>
public readonly record struct LeaseDecision(bool Granted, ushort Owner, string Reason);
