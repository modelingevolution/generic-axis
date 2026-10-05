using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// One-verb mode (protocol § Conformance checks, One-verb mode; ADR-38): pre-flight, map and limits check, guards,
/// lease and beat, one verb through the handshake, every 20 ms status read printed until the verb completes, then the
/// cleanup of § Rules for every run. It reuses the checker's <see cref="CheckContext"/> (channel, beat, command writer,
/// poller) and <see cref="CommanderSession"/> (pre-flight, cleanup): there is no second implementation.
/// </summary>
public sealed class CommandRunner(ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan StateBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HomeBudget = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan JogEntryBudget = TimeSpan.FromMilliseconds(500);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const ushort Disabled = 0, Standstill = 1, ContinuousMotion = 4, ErrorStop = 7;

    private readonly ILogger _log = loggerFactory.CreateLogger<CommandRunner>();

    /// <summary>Runs <see cref="CheckerOptions.Command"/> against the PLC; returns the protocol's exit code.</summary>
    public Task<int> RunAsync(CheckerOptions options, TextWriter output, CancellationToken ct) =>
        RunAsync(options, new ModbusChannel(options.Host, options.Port, loggerFactory.CreateLogger<ModbusChannel>(), map: options.Map), output, ct);

    internal async Task<int> RunAsync(CheckerOptions options, IModbusChannel channel, TextWriter output, CancellationToken ct)
    {
        var request = options.Command ?? throw new ArgumentException("no --command", nameof(options));
        var name = request.Name;
        await using var ctx = new CheckContext(options, channel, _log);
        var run = new Run(ctx, request, output);
        output.WriteLine($"--command {Describe(request)} on {options.Host}:{options.Port} unit {options.Unit} "
                         + $"(C = holding {options.CommandBase}, S = input {options.StatusBase}), owner {options.OwnerId}");

        int exit;
        string result;
        try
        {
            CommanderSession.Preflight preflight;
            try
            {
                // #64: the note states what was seen; the clear is reported when written (after the guards and the lease).
                preflight = await CommanderSession.PreflightAsync(ctx, _log, ct, deadHolder: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Finish(output, $"{name}: interrupted by the operator during pre-flight; nothing was written.", ConformanceExitCodes.Interrupted);
            }

            if (preflight.Unreadable is { } unreadable)
                return Finish(output, $"{name}: {Failure.FromMotion(unreadable).Render()}", ConformanceExitCodes.Fail);
            if (preflight.Refusal is { } refusal)
                return Finish(output, $"Pre-flight: {refusal}", ConformanceExitCodes.Refused);
            if (preflight.Note is { } note) output.WriteLine($"Pre-flight: {note}");

            (exit, result) = await run.ExecuteAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            (exit, result) = run.JogObserved
                ? (ConformanceExitCodes.Pass, $"{name}: ended by the operator after ContinuousMotion was observed.")
                : (ConformanceExitCodes.Interrupted, $"{name}: interrupted by the operator before the verb completed.");
        }
        catch (MotionException ex)
        {
            (exit, result) = (ConformanceExitCodes.Fail, $"{name}: {Failure.FromMotion(ex).Render()}");
        }
        finally
        {
            // The verb's prints end where the verb ends; cleanup is journalled instead.
            ctx.Observer = null;
        }

        output.WriteLine(result);
        // protocol step 6: Stop if moving, clear edges, Enable 0 only if this run set Enable 1, release the lease.
        await CommanderSession.CleanupAsync(ctx, _log, disableOnExit: run.DisableOnExit);
        foreach (var entry in ctx.CleanupLog) output.WriteLine($"Cleanup: {entry}");
        return Result(output, exit);
    }

    /// <summary>
    /// The last line of a run per exit code (protocol § One-verb mode): the one table the runner prints from, bound to
    /// protocol.md by a parity test (#41). A usage error never reaches the runner and prints no RESULT line.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ResultWords { get; } = new Dictionary<int, string>
    {
        [ConformanceExitCodes.Pass] = "PASS",
        [ConformanceExitCodes.Fail] = "FAIL",
        [ConformanceExitCodes.Usage] = "GUARD",
        [ConformanceExitCodes.Refused] = "REFUSED",
        [ConformanceExitCodes.Interrupted] = "INTERRUPTED",
    };

    private static int Finish(TextWriter output, string line, int exit)
    {
        output.WriteLine(line);
        return Result(output, exit);
    }

    private static int Result(TextWriter output, int exit)
    {
        output.WriteLine($"RESULT: {ResultWords[exit]}");
        return exit;
    }

    private static string Describe(VerbRequest r) => r.Verb switch
    {
        Verb.Move => $"move {r.Target!.Value.ToString("0.######", Inv)} --speed {r.SpeedPercent.ToString("0.###", Inv)}",
        Verb.Jog => $"jog {r.Velocity!.Value.ToString("0.######", Inv)}{(r.For is { } f ? $" --for {f.TotalSeconds.ToString("0.###", Inv)}" : "")}",
        _ => r.Name,
    };

    /// <summary>Unit conversion of the protocol: raw register value ÷ 1000.</summary>
    private static string U(int raw) => Words.FromRaw(raw).ToString("0.000", Inv);

    /// <summary>One status line (protocol step 5): ms since the verb's first write, then the six fields.</summary>
    internal static string StatusLine(long ms, PlcView v)
    {
        var s = v.Status;
        return $"+{ms,6} ms  State {s.State} {RegisterDump.StateName(s.State),-16} Flags {(s.Flags == 0 ? "none" : s.Flags.ToString().Replace(", ", "|", StringComparison.Ordinal)),-40} "
               + $"ActualPosition {U(s.ActualPosition),11}  ActualVelocity {U(s.ActualVelocity),10}  FaultCode {s.FaultCode} {RegisterDump.FaultName(s.FaultCode)}  CommandAck {s.CommandAck}";
    }

    /// <summary>The state of one run: the verb, the level found, whether this run energised the drive.</summary>
    private sealed class Run(CheckContext ctx, VerbRequest request, TextWriter output)
    {
        private Stopwatch? _clock;
        private long _writtenAt;
        private bool _bit;
        private bool _foundEnergised;
        private bool _wroteEnable1;

        /// <summary>
        /// Protocol § One-verb mode step 4 (#61): cleanup writes Enable 0 if and only if the axis was found not energised
        /// (decided from State at step 2, never from the command bit) and this run wrote Enable 1.
        /// </summary>
        public bool DisableOnExit => !_foundEnergised && _wroteEnable1;

        /// <summary>A jog reached ContinuousMotion: an operator's Ctrl-C after this point is the jog's normal end.</summary>
        public bool JogObserved { get; private set; }

        private string Name => request.Name;

        /// <summary>The Enable level the command word holds now, kept on Stop writes.</summary>
        private CommandBits Level => _bit ? CommandBits.Enable : CommandBits.None;

        public async Task<(int Exit, string Line)> ExecuteAsync(CancellationToken ct)
        {
            // Step 2: the status block before anything is written.
            var s = await ctx.ReadStatusAsync(ct);
            if (s.MapVersion != RegisterMap.Version)
                return Fail(Failure.Protocol($"wrong map version. Read MapVersion ({ctx.At(RegisterField.MapVersion)}) = {s.MapVersion}, expected {RegisterMap.Version}; nothing written."));
            if (s.LimitsPublished && !s.LimitsValid)
                return Fail(Failure.Protocol($"limits not sane. Read TravelMin ({ctx.At(RegisterField.TravelMin)}) = {s.TravelMin}, TravelMax ({ctx.At(RegisterField.TravelMax)}) = {s.TravelMax}, "
                                             + $"MaxVelocity ({ctx.At(RegisterField.MaxVelocity)}) = {s.MaxVelocity}, expected TravelMin < TravelMax and MaxVelocity > 0; nothing written."));

            // Step 3: guards, before any write — the lease included.
            if (Guard(s) is { } refused) return (ConformanceExitCodes.Usage, $"{Name}: {refused.Render()}");

            await ctx.TakeLeaseAsync(ct);
            if (ctx.ForeignTrip)
            {
                // Step 1: a dead holder's trip — proceed as the driver does at attach; ErrorStop and FaultCode 4 stay for reset.
                await ctx.ClearWatchdogFaultAsync(ct);
                output.WriteLine($"{ctx.At(RegisterField.WatchdogFault)} = 0 written at attach, as the driver does (ErrorStop and FaultCode 4 stay for reset)");
            }

            await ctx.Beater.StartAsync(ct);
            _bit = (await ctx.Commands.ReadCommandWordAsync(ct) & (ushort)CommandBits.Enable) != 0;
            _foundEnergised = s.State is not (Disabled or ErrorStop);
            ctx.Observer = v => output.WriteLine(StatusLine(_clock is { } c ? (long)c.Elapsed.TotalMilliseconds : 0, v));

            var failure = request.Verb switch
            {
                Verb.Enable => await EnableAsync(ct),
                Verb.Disable => await DisableAsync(ct),
                Verb.Home => await HomeAsync(ct),
                Verb.Stop => await StopAsync(ct),
                Verb.Reset => await ResetAsync(ct),
                Verb.Move => await MoveAsync(s, ct),
                _ => await JogAsync(s, ct),
            };
            return failure is null ? (ConformanceExitCodes.Pass, _done ?? $"{Name}: done.") : Fail(failure);
        }

        private string? _done;

        private (int, string) Fail(Failure f) => (ConformanceExitCodes.Fail, $"{Name}: {f.Render()}");

        // ---- guards (protocol step 3; Commander class) -------------------------------------------------------------

        private Failure? Guard(StatusBlock s)
        {
            if (request.Verb is not (Verb.Move or Verb.Jog)) return null;
            if (!s.LimitsPublished)
                return Commander("OutOfRange", $"the PLC publishes no limits; {Name} needs them. Read TravelMin ({ctx.At(RegisterField.TravelMin)}) = 0, "
                                               + $"TravelMax ({ctx.At(RegisterField.TravelMax)}) = 0, MaxVelocity ({ctx.At(RegisterField.MaxVelocity)}) = 0.");
            if (request.Verb == Verb.Move)
            {
                if (!s.Homed)
                    return Commander("NotHomed", $"move needs Homed. Read Flags ({ctx.At(RegisterField.Flags)}) = 0x{(ushort)s.Flags:X4}, expected bit 0 (Homed) set.");
                // Judged on the raw values the PLC would be sent (#37): what is written is the rounded register value.
                var target = request.Target!.Value;
                var targetRaw = Raw(target);
                if (targetRaw < s.TravelMin || targetRaw > s.TravelMax)
                    return Commander("OutOfRange", $"target {target.ToString("0.000######", Inv)} (raw {targetRaw.ToString("0", Inv)}) is outside TravelMin..TravelMax. Read TravelMin ({ctx.At(RegisterField.TravelMin)}) = {s.TravelMin}, "
                                                   + $"TravelMax ({ctx.At(RegisterField.TravelMax)}) = {s.TravelMax}.");
                var pct = request.SpeedPercent;
                if (!(pct > 0 && pct <= 100))
                    return Commander("UnreachableSpeed", $"speed {pct.ToString("0.###", Inv)} % outside 0 < pct ≤ 100.");
                if (SpeedRounding.Raw(pct, s.MaxVelocity) == 0)
                    return Commander("UnreachableSpeed", SpeedRounding.Refusal(pct, s.MaxVelocity, ctx.At(RegisterField.MaxVelocity)));
                return null;
            }

            var v = request.Velocity!.Value;
            var raw = Raw(v);
            if (raw == 0 || Math.Abs(raw) > s.MaxVelocity)
                return Commander("UnreachableSpeed", $"jog needs 0 < |v| ≤ MaxVelocity, got {v.ToString("0.000######", Inv)} (raw {raw.ToString("0", Inv)}). Read MaxVelocity ({ctx.At(RegisterField.MaxVelocity)}) = {s.MaxVelocity}.");
            return null;
        }

        /// <summary>The register value <paramref name="units"/> is written as (0.001 per count, half away from zero, as
        /// <see cref="Words.ToRaw"/>), as a double so an over-range value is judged, not thrown.</summary>
        private static double Raw(double units) => Math.Round(units * Words.Scale, MidpointRounding.AwayFromZero) + 0.0; // + 0.0: no "-0"

        private static Failure Commander(string name, string text) => new(ErrorClass.Commander, name, 7, $"refused before writing anything: {text}");

        // ---- the verbs (protocol step 4 and 5) --------------------------------------------------------------------

        private async Task<Failure?> EnableAsync(CancellationToken ct)
        {
            // #61: bit 0 already 1 while Disabled (a pendant Reset after a trip) — Enable 0 first, acked, so the Enable is
            // a fresh 0→1 edge, as the driver's EnergiseAsync does.
            if (CommandWriter.NeedsFreshEdge(await ctx.Commands.ReadCommandWordAsync(ct), (await ctx.ReadStatusAsync(ct)).State)
                && await SendAsync(CommandBits.None, "Enable 0 (before a fresh Enable edge)", ct) is { } fresh) return fresh;
            if (await SendAsync(CommandBits.Enable, "Enable 1", ct) is { } f) return f;
            return await AwaitAsync(v => v.State == Standstill, StateBudget, ct,
                v => Failure.Machine("DriveFault", $"no Standstill 5 s after Enable 1. Read State ({ctx.At(RegisterField.State)}) = {v.State}, expected 1."), "Standstill");
        }

        private async Task<Failure?> DisableAsync(CancellationToken ct)
        {
            if (await SendAsync(CommandBits.None, "Enable 0", ct) is { } f) return f;
            return await AwaitAsync(v => v.State == Disabled, StateBudget, ct,
                v => Failure.Machine("DriveFault", $"no Disabled 5 s after Enable 0. Read State ({ctx.At(RegisterField.State)}) = {v.State}, expected 0."), "Disabled", faultEnds: false);
        }

        private async Task<Failure?> HomeAsync(CancellationToken ct)
        {
            if (await EnsureEnabledAsync(ct) is { } f) return f;
            if (await SendAsync(CommandBits.Enable | CommandBits.Home, "Home", ct) is { } h) return h;
            return await AwaitAsync(v => v.State == Standstill && v.Status.Homed, HomeBudget, ct,
                v => Failure.Machine("HomeLatchFailed", $"not homed within 120 s. Read State ({ctx.At(RegisterField.State)}) = {v.State}, Flags.Homed = {(v.Status.Homed ? 1 : 0)}, expected 1 and 1."),
                "Standstill + Homed");
        }

        private async Task<Failure?> StopAsync(CancellationToken ct)
        {
            if (await SendAsync(Level | CommandBits.Stop, "Stop", ct, ChannelPriority.Stop) is { } f) return f;
            return await AwaitAsync(v => v.State is Standstill or Disabled, StateBudget, ct,
                v => Failure.Machine("MotionFailed", $"still moving 5 s after Stop. Read State ({ctx.At(RegisterField.State)}) = {v.State}, ActualVelocity ({ctx.At(RegisterField.ActualVelocity)}) = {v.Status.ActualVelocity}, expected 0 or 1."),
                "Standstill or Disabled");
        }

        private async Task<Failure?> ResetAsync(CancellationToken ct)
        {
            var v0 = await ctx.ReadViewAsync(ct);
            if (v0.WatchdogFault != 0)
            {
                await ctx.ClearWatchdogFaultAsync(ct);
                output.WriteLine($"{ctx.At(RegisterField.WatchdogFault)} = 0 written before the Reset");
            }

            if (Level != CommandBits.None && await SendAsync(CommandBits.None, "Enable 0", ct) is { } off) return off;

            if (await SendAsync(CommandBits.Reset, "Reset", ct) is { } f) return f;
            return await AwaitAsync(v => v.State != ErrorStop, StateBudget, ct,
                v => Failure.Fault(v.Status.FaultCode, ctx.At(RegisterField.FaultCode)) with
                {
                    Text = $"still in ErrorStop 5 s after Reset. Read State ({ctx.At(RegisterField.State)}) = 7, FaultCode ({ctx.At(RegisterField.FaultCode)}) = {v.Status.FaultCode}.",
                },
                "not ErrorStop", faultEnds: false);
        }

        private async Task<Failure?> MoveAsync(StatusBlock s, CancellationToken ct)
        {
            var target = Words.ToRaw(request.Target!.Value, "TargetPosition");
            var velocity = SpeedRounding.Raw(request.SpeedPercent, s.MaxVelocity); // the guard refused 0
            var budget = TimeSpan.FromSeconds(2.0 * Math.Abs(target - (double)s.ActualPosition) / velocity + 5);
            if (await EnsureEnabledAsync(ct) is { } f) return f;
            await ctx.Commands.WriteParametersAsync(target, velocity, 0, ct);
            if (await SendAsync(CommandBits.Enable | CommandBits.MoveAbsolute, "MoveAbsolute", ct) is { } m) return m;
            return await AwaitAsync(v => v.State == Standstill && v.Status.InPosition, budget, ct,
                v => Failure.Machine("MotionFailed", $"not arrived within {budget.TotalSeconds:0.0} s (2 × |target − start| ÷ velocity + 5 s). Read State ({ctx.At(RegisterField.State)}) = {v.State}, "
                                                     + $"Flags.InPosition = {(v.Status.InPosition ? 1 : 0)}, ActualPosition ({ctx.At(RegisterField.ActualPosition)}) = {v.Status.ActualPosition}, expected 1, 1, {target}."),
                "Standstill + InPosition");
        }

        private async Task<Failure?> JogAsync(StatusBlock s, CancellationToken ct)
        {
            var velocity = Words.ToRaw(request.Velocity!.Value, "Velocity");
            if (await EnsureEnabledAsync(ct) is { } f) return f;
            await ctx.Commands.WriteParametersAsync(s.ActualPosition, velocity, 0, ct);
            if (await SendAsync(CommandBits.Enable | CommandBits.MoveVelocity, "MoveVelocity", ct) is { } j) return j;
            if (await AwaitAsync(v => v.State == ContinuousMotion, JogEntryBudget, ct,
                    v => Failure.Machine("MotionFailed", $"no ContinuousMotion 500 ms after the ack. Read State ({ctx.At(RegisterField.State)}) = {v.State}, expected 4."),
                    "ContinuousMotion", fromAck: true) is { } entry) return entry;
            JogObserved = true;

            // Keep printing until --for elapses or Ctrl-C; either way the verb itself sends the Stop (protocol step 5), on its
            // own budget so the operator's Ctrl-C cannot cancel it, and cleanup finds nothing moving.
            PlcView? endedByPlc = null;
            long elapsed = 0;
            var byOperator = false;
            try
            {
                var watch = await ctx.WaitForAsync(v => v.State != ContinuousMotion, request.For ?? TimeSpan.FromDays(1), CheckContext.Now(), ct);
                if (watch.Met) (endedByPlc, elapsed) = (watch.View, watch.ElapsedMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                byOperator = true;
            }

            if (endedByPlc is { } plc)
            {
                if (Unexpected(plc) is { } fault) return fault;
                _done = $"{Name}: the PLC ended the jog itself after {elapsed} ms (State {plc.State} {RegisterDump.StateName(plc.State)}).";
                return null;
            }

            using var stopBudget = new CancellationTokenSource(StateBudget + TimeSpan.FromSeconds(2));
            if (await SendAsync(CommandBits.Enable | CommandBits.Stop, "Stop", stopBudget.Token, ChannelPriority.Stop) is { } stop) return stop;
            var halted = await AwaitAsync(v => v.State is Standstill or Disabled, StateBudget, stopBudget.Token,
                v => Failure.Machine("MotionFailed", $"still moving 5 s after Stop. Read State ({ctx.At(RegisterField.State)}) = {v.State}, expected 0 or 1."), "Standstill");
            if (halted is null)
                _done = byOperator
                    ? $"{Name}: ended by the operator after ContinuousMotion was observed; Stop sent: halted."
                    : $"{Name}: done — ContinuousMotion for {request.For!.Value.TotalSeconds:0.###} s (--for), then Stop: halted.";
            return halted;
        }

        // ---- shared steps ---------------------------------------------------------------------------------------

        /// <summary>Protocol step 4: <c>home</c>, <c>move</c> and <c>jog</c> from Disabled set Enable first.</summary>
        private async Task<Failure?> EnsureEnabledAsync(CancellationToken ct)
        {
            var s = await ctx.ReadStatusAsync(ct);
            if (s.State != Disabled) return null;
            return await EnableAsync(ct);
        }

        /// <summary>The handshake through the checker's <see cref="CommandWriter"/>; a missing ack is NotAcknowledged.</summary>
        private async Task<Failure?> SendAsync(CommandBits bits, string what, CancellationToken ct, ChannelPriority lane = ChannelPriority.Move)
        {
            _clock ??= Stopwatch.StartNew();
            _bit = (bits & CommandBits.Enable) != 0;
            _wroteEnable1 |= _bit;
            output.WriteLine($"+{(long)_clock.Elapsed.TotalMilliseconds,6} ms  write Command {(bits == CommandBits.None ? "none" : bits.ToString().Replace(", ", "|", StringComparison.Ordinal))} (0x{(ushort)bits:X4})");
            var ack = await ctx.Commands.SendAsync(bits, ct, lane);
            _writtenAt = ack.WrittenAt;
            return ack.Acked ? null : Failure.NotAcknowledged(what, ack.Seq, ack.View.Status.CommandAck, ack.View.State);
        }

        /// <summary>
        /// Prints every read until <paramref name="until"/> holds. A PLC fault (ErrorStop with a FaultCode) or a state
        /// outside the protocol ends the wait at once with its own class, unless <paramref name="faultEnds"/> is off.
        /// </summary>
        private async Task<Failure?> AwaitAsync(Func<PlcView, bool> until, TimeSpan budget, CancellationToken ct,
            Func<PlcView, Failure> late, string expected, bool faultEnds = true, bool fromAck = false)
        {
            // Measured from the completion of the triggering write (protocol § Rules, Timing); jog entry from the ack.
            var since = fromAck ? CheckContext.Now() : _writtenAt;
            var w = await ctx.WaitForAsync(v => until(v) || (faultEnds && Unexpected(v) is not null), budget, since, ct);
            if (!w.Met) return late(w.View);
            if (!until(w.View)) return Unexpected(w.View);
            _done = $"{Name}: done — {expected} after {w.ElapsedMs} ms.";
            return null;
        }

        private Failure? Unexpected(PlcView v) =>
            v.State is 5 or > 7 || (v.State == ErrorStop && v.Status.FaultCode == 0)
                ? Failure.InvalidState(v.State, v.Status.FaultCode, ctx.At(RegisterField.State))
                : v.State == ErrorStop ? Failure.Fault(v.Status.FaultCode, ctx.At(RegisterField.FaultCode)) : null;
    }
}
