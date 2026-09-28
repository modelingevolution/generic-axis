using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>GA-U-46 — the connector's reporting and retry policy (design.md § Plugin).</summary>
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

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLogger _log = new();
    private readonly GenericAxisConnector _connector;
    private readonly DeviceId _id = DeviceId.New(GenericAxisPlugin.LinearTrackDeviceType);

    public ConnectorTests() => _connector = new GenericAxisConnector(logger: _log, timeProvider: _time);

    private static Func<CancellationToken, Task> Throws(Exception ex) => _ => Task.FromException(ex);

    [Fact]
    public async Task GA_U_46_A_Held_Lease_Is_Information_And_Retried_On_The_Next_Tick()
    {
        await _connector.AttachAsync(_id, 1,
            Throws(new MotionException(MotionError.LeaseHeld, "LeaseOwner is 3", "carriage")), CancellationToken.None);

        _log.Entries.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(e =>
                e.Level == LogLevel.Information && e.Message.Contains("MotionError.LeaseHeld: LeaseOwner is 3"));
        _connector.IsDue(_id).Should().BeTrue("a held lease is retried on the next 1 s tick");
    }

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public async Task GA_U_46_A_Socket_Failure_Twice_Logs_One_Warning_Then_One_Debug(Type failure)
    {
        var ex = (Exception)Activator.CreateInstance(failure)!;

        await _connector.AttachAsync(_id, 1, Throws(ex), CancellationToken.None);
        _time.Advance(GenericAxisConnector.FailureRetryInterval);
        await _connector.AttachAsync(_id, 1, Throws(ex), CancellationToken.None);

        _log.Entries.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
    }

    [Fact]
    public async Task GA_U_46_A_Failure_Other_Than_A_Held_Lease_Waits_Five_Seconds()
    {
        await _connector.AttachAsync(_id, 1, Throws(new SocketException()), CancellationToken.None);

        _connector.IsDue(_id).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(4.9));
        _connector.IsDue(_id).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(0.1));
        _connector.IsDue(_id).Should().BeTrue();
    }

    [Fact]
    public async Task A_Motion_Failure_Is_Logged_With_Its_Error_Class_And_Verbatim_Message()
    {
        const string message = "carriage: PLC serves map version 2; this driver speaks 1 (register S+14)";

        await _connector.AttachAsync(_id, 1,
            Throws(new MotionException(MotionError.CommunicationLost, message, "carriage")), CancellationToken.None);

        _log.Entries.Should().ContainSingle()
            .Which.Message.Should().Contain($"MotionError.CommunicationLost: {message}");
    }

    [Fact]
    public async Task A_Machine_Refusal_Keeps_Its_Own_Class_And_Is_Not_Called_A_Connection_Problem()
    {
        const string message = "carriage: TravelMin 10 is not below TravelMax 5 (registers S+8..S+11)";

        await _connector.AttachAsync(_id, 1,
            Throws(new MotionException(MotionError.OutOfRange, message, "carriage")), CancellationToken.None);

        var entry = _log.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain($"MotionError.OutOfRange: {message}");
        entry.Message.Should().NotContainAny("CommunicationLost", "connection", "unreachable");
    }

    [Fact]
    public async Task A_Raw_Socket_Failure_Is_Logged_By_Its_Exception_Type_And_Verbatim_Message()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);

        await _connector.AttachAsync(_id, 1, Throws(ex), CancellationToken.None);

        _log.Entries.Should().ContainSingle()
            .Which.Message.Should().Contain($"System.Net.Sockets.SocketException: {ex.Message}");
    }

    [Fact]
    public async Task The_Debug_Repeat_Carries_The_Same_Class_And_Message()
    {
        const string message = "carriage: PLC did not acknowledge CommandSeq 12 within 500 ms";
        var ex = new MotionException(MotionError.CommunicationLost, message, "carriage");

        await _connector.AttachAsync(_id, 1, Throws(ex), CancellationToken.None);
        await _connector.AttachAsync(_id, 1, Throws(ex), CancellationToken.None);

        _log.Entries[1].Level.Should().Be(LogLevel.Debug);
        _log.Entries[1].Message.Should().Contain($"MotionError.CommunicationLost: {message}");
    }

    [Fact]
    public async Task A_Recovered_Machine_Is_Loud_Again_On_Its_Next_Outage()
    {
        await _connector.AttachAsync(_id, 1, Throws(new SocketException()), CancellationToken.None);
        await _connector.AttachAsync(_id, 1, _ => Task.CompletedTask, CancellationToken.None);
        await _connector.AttachAsync(_id, 1, Throws(new SocketException()), CancellationToken.None);

        _log.Entries.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Information, LogLevel.Warning);
        _connector.IsDue(_id).Should().BeFalse();
    }

    [Fact]
    public async Task Two_Machines_Are_Suppressed_Independently()
    {
        var other = DeviceId.New(GenericAxisPlugin.PositionerDeviceType);

        await _connector.AttachAsync(_id, 1, Throws(new SocketException()), CancellationToken.None);
        await _connector.AttachAsync(other, 1, Throws(new SocketException()), CancellationToken.None);

        _log.Entries.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Warning);
    }

    [Fact]
    public async Task Forget_Re_Arms_The_Warning_And_Clears_The_Backoff()
    {
        await _connector.AttachAsync(_id, 1, Throws(new SocketException()), CancellationToken.None);
        _connector.Forget(_id);

        _connector.IsDue(_id).Should().BeTrue();
        await _connector.AttachAsync(_id, 1, Throws(new SocketException()), CancellationToken.None);
        _log.Entries.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Warning);
    }

    [Fact]
    public async Task Cancellation_While_Stopping_Is_Not_Reported_As_A_Failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var attach = () => _connector.AttachAsync(_id, 1,
            ct => Task.FromCanceled(ct), cts.Token);

        await attach.Should().ThrowAsync<OperationCanceledException>();
        _log.Entries.Should().BeEmpty();
    }
}
