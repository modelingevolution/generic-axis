using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>
/// GA-U-46 and protocol.md § Errors and debugging — the connector reports each attach failure by its
/// error class, with the driver's message verbatim, and retries on the class's schedule.
/// </summary>
public sealed class ConnectorTests
{
    private sealed class RecordingLogger : ILogger<GenericAxisConnector>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, formatter(state, ex)));
    }

    // Driver messages carry the facts only; the class word is the connector's prefix (lead ruling).
    private const string MapVersionMessage =
        "carriage: attach refused. Read MapVersion (S+14 = 114) = 2, expected 1.";
    private const string RefusedMessage =
        "carriage: read S+0…S+14 on 192.168.58.20:502 unit 1 failed twice (reconnected once): Connection refused.";
    private const string LeaseMessage =
        "carriage: attach refused. Read LeaseOwner (C+9 = 9) = 3, Heartbeat changing.";

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLogger _log = new();
    private readonly GenericAxisConnector _connector;
    private readonly DeviceId _id = DeviceId.New(GenericAxisPlugin.LinearTrackDeviceType);

    public ConnectorTests() => _connector = new GenericAxisConnector(logger: _log, timeProvider: _time);

    private static Func<CancellationToken, Task> Throws(Exception ex) => _ => Task.FromException(ex);

    private Task Attach(Exception ex) => _connector.AttachAsync(_id, 1, Throws(ex), CancellationToken.None);

    private IEnumerable<LogLevel> Levels => _log.Entries.Select(e => e.Level);

    // ── Commander / LeaseHeld ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GA_U_46_A_Held_Lease_Is_Information_And_Retried_On_The_Next_Tick()
    {
        await Attach(new MotionException(MotionError.LeaseHeld, LeaseMessage, "carriage"));

        _log.Entries.Should().ContainSingle().Which.Should().Be(
            (LogLevel.Information, $"Commander: generic axis {_id} did not attach: {LeaseMessage} (next attempt in 1 s)"));
        _connector.IsDue(_id).Should().BeTrue("a held lease is retried on the next 1 s tick");
    }

    [Fact]
    public async Task GA_U_80_A_Held_Lease_After_An_Outage_Is_Retried_In_1_s_Not_5_s_And_Never_Warns()
    {
        // The deny path: the PLC answers again but a live commander holds the lease. The 5 s transport
        // back-off must not carry over, and every refusal is Information — never Warning or Error, and
        // never suppressed to Debug, so the operator sees each time who holds the axis.
        await Attach(new SocketException((int)SocketError.ConnectionRefused));
        _connector.IsDue(_id).Should().BeFalse();
        _time.Advance(GenericAxisConnector.FailureRetryInterval);

        var leaseLine = (LogLevel.Information,
            $"Commander: generic axis {_id} did not attach: {LeaseMessage} (next attempt in 1 s)");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Attach(new MotionException(MotionError.LeaseHeld, LeaseMessage, "carriage"));
            _connector.IsDue(_id).Should().BeTrue("a held lease is retried on the next 1 s tick, not after 5 s");
            _time.Advance(GenericAxisConnector.TickInterval);
        }

        Levels.Should().Equal(LogLevel.Warning, LogLevel.Information, LogLevel.Information, LogLevel.Information);
        _log.Entries.Skip(1).Should().AllBeEquivalentTo(leaseLine);
        GenericAxisConnector.TickInterval.Should().Be(TimeSpan.FromSeconds(1));
        GenericAxisConnector.FailureRetryInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GA_U_81_The_Suppressed_Debug_Line_Has_The_Same_Shape_As_The_Warning()
    {
        await Attach(new MotionException(MotionError.CommunicationLost, RefusedMessage, "carriage"));
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        await Attach(new MotionException(MotionError.CommunicationLost, RefusedMessage, "carriage"));

        var line = $"Transport: generic axis {_id} did not attach: {RefusedMessage} (next attempt in 5 s)";
        _log.Entries.Should().Equal((LogLevel.Warning, line), (LogLevel.Debug, line));
    }

    // ── Transport ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public async Task GA_U_46_A_Socket_Failure_Twice_Logs_One_Warning_Then_One_Debug(Type failure)
    {
        var ex = (Exception)Activator.CreateInstance(failure)!;

        await Attach(ex);
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        await Attach(ex);

        Levels.Should().Equal(LogLevel.Warning, LogLevel.Debug);
        _log.Entries.Should().OnlyContain(e =>
            e.Message.StartsWith("Transport: ") && e.Message.Contains($"{failure.FullName}: {ex.Message}"));
    }

    [Fact]
    public async Task A_Communication_Loss_Is_Transport_With_The_Driver_Message_Verbatim()
    {
        await Attach(new MotionException(MotionError.CommunicationLost, RefusedMessage, "carriage"));

        _log.Entries.Should().ContainSingle().Which.Should().Be(
            (LogLevel.Warning, $"Transport: generic axis {_id} did not attach: {RefusedMessage} (next attempt in 5 s)"));
    }

    [Fact]
    public async Task GA_U_46_A_Transport_Failure_Waits_Five_Seconds()
    {
        await Attach(new SocketException((int)SocketError.ConnectionRefused));

        _connector.IsDue(_id).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(4.9));
        _connector.IsDue(_id).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(0.1));
        _connector.IsDue(_id).Should().BeTrue();
    }

    // ── Protocol ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Protocol_Mismatch_Logs_Error_On_Every_Attempt_And_Never_Reads_Like_A_Cable_Fault()
    {
        var ex = new MotionException(MotionError.ProtocolMismatch, MapVersionMessage, "carriage");

        await Attach(ex);
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        await Attach(ex);
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        await Attach(ex);

        Levels.Should().Equal(LogLevel.Error, LogLevel.Error, LogLevel.Error);
        _log.Entries.Should().OnlyContain(e =>
            e.Message == $"Protocol: generic axis {_id} did not attach: {MapVersionMessage} (next attempt in 5 s)");
    }

    [Fact]
    public async Task A_Protocol_Mismatch_Is_Retried_Every_Five_Seconds_So_A_Corrected_Plc_Attaches()
    {
        await Attach(new MotionException(MotionError.ProtocolMismatch, MapVersionMessage, "carriage"));

        _connector.IsDue(_id).Should().BeFalse();
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        _connector.IsDue(_id).Should().BeTrue();

        await _connector.AttachAsync(_id, 1, _ => Task.CompletedTask, CancellationToken.None);
        _log.Entries.Last().Should().Be((LogLevel.Information, $"Generic axis {_id} attached as owner 1"));
    }

    [Fact]
    public async Task A_Protocol_Error_Between_Transport_Errors_Does_Not_Consume_The_Transport_Warning()
    {
        await Attach(new MotionException(MotionError.ProtocolMismatch, MapVersionMessage, "carriage"));
        await Attach(new SocketException((int)SocketError.ConnectionRefused));

        Levels.Should().Equal(LogLevel.Error, LogLevel.Warning);
    }

    // ── Commander (other than LeaseHeld) ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_Reading_Range_That_Contradicts_The_Machine_Logs_Error_On_Every_Attempt()
    {
        const string message =
            "carriage: attach refused. Read TravelMin (S+8) = 0, configured ReadMin = 5, expected ReadMin ≤ TravelMin.";
        var ex = new MotionException(MotionError.OutOfRange, message, "carriage");

        await Attach(ex);
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        await Attach(ex);

        Levels.Should().Equal(LogLevel.Error, LogLevel.Error);
        _log.Entries.Should().OnlyContain(e =>
            e.Message == $"Commander: generic axis {_id} did not attach: {message} (next attempt in 5 s)");
        _connector.IsDue(_id).Should().BeFalse("it is retried every 5 s, so a corrected configuration attaches");
    }

    // ── Machine and unclassified ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Machine_Fault_Keeps_Its_Class_And_Follows_The_Warn_Once_Rule()
    {
        const string message = "carriage: Read FaultCode (S+6 = 106) = 1.";
        var ex = new MotionException(MotionError.DriveFault, message, "carriage");

        await Attach(ex);
        await Attach(ex);

        Levels.Should().Equal(LogLevel.Warning, LogLevel.Debug);
        _log.Entries.Should().OnlyContain(e => e.Message.StartsWith("Machine: ") && e.Message.Contains(message));
    }

    [Fact]
    public async Task A_Non_Motion_Non_Transport_Failure_Claims_No_Class_And_Logs_Error_Every_Attempt()
    {
        var ex = new ArgumentException("carriage: ReadMin -20 is above the PLC's TravelMin -30", "ReadMin");

        await Attach(ex);
        await Attach(ex);

        Levels.Should().Equal(LogLevel.Error, LogLevel.Error);
        _log.Entries.Should().OnlyContain(e =>
            e.Message.StartsWith("System.ArgumentException: ") && e.Message.Contains(ex.Message)
            && !e.Message.Contains("Transport"));
    }

    // ── Suppression bookkeeping ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Recovered_Machine_Is_Loud_Again_On_Its_Next_Outage()
    {
        await Attach(new SocketException());
        await _connector.AttachAsync(_id, 1, _ => Task.CompletedTask, CancellationToken.None);
        await Attach(new SocketException());

        Levels.Should().Equal(LogLevel.Warning, LogLevel.Information, LogLevel.Warning);
        _connector.IsDue(_id).Should().BeFalse();
    }

    [Fact]
    public async Task Two_Machines_Are_Suppressed_Independently()
    {
        var other = DeviceId.New(GenericAxisPlugin.PositionerDeviceType);

        await Attach(new SocketException());
        await _connector.AttachAsync(other, 1, Throws(new SocketException()), CancellationToken.None);

        Levels.Should().Equal(LogLevel.Warning, LogLevel.Warning);
    }

    [Fact]
    public async Task Forget_Re_Arms_The_Warning_And_Clears_The_Backoff()
    {
        await Attach(new SocketException());
        _connector.Forget(_id);

        _connector.IsDue(_id).Should().BeTrue();
        await Attach(new SocketException());
        Levels.Should().Equal(LogLevel.Warning, LogLevel.Warning);
    }

    [Fact]
    public async Task Cancellation_While_Stopping_Is_Not_Reported_As_A_Failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var attach = () => _connector.AttachAsync(_id, 1, ct => Task.FromCanceled(ct), cts.Token);

        await attach.Should().ThrowAsync<OperationCanceledException>();
        _log.Entries.Should().BeEmpty();
    }
}
