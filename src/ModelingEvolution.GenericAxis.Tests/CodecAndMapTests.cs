using FluentAssertions;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — Codec and map (GA-U-01 … GA-U-05).</summary>
public class CodecAndMapTests
{
    [Fact(DisplayName = "GA-U-01 32-bit values are low word first")]
    public void Split_NegativeValue_LowWordFirst()
    {
        Words.Split(-2_500_000).Should().Be(((ushort)0xDA60, (ushort)0xFFD9));
        Words.Join(0xDA60, 0xFFD9).Should().Be(-2_500_000);

        Span<ushort> pair = stackalloc ushort[2];
        Words.Write(pair, 65_538);
        pair.ToArray().Should().Equal((ushort)0x0002, (ushort)0x0001);
        Words.Read(pair).Should().Be(65_538);
    }

    [Fact(DisplayName = "GA-U-02 Units scale by 1000 and round half away from zero")]
    public void ToRaw_ScalesRoundsAndRefusesOverflow()
    {
        Words.ToRaw(12.3456, "x").Should().Be(12_346);
        Words.ToRaw(-0.0005, "x").Should().Be(-1);
        Words.ToRaw(0.0005, "x").Should().Be(1);
        Words.FromRaw(12_346).Should().Be(12.346);

        var overflow = () => Words.ToRaw(2_147_483.648, "target", "carriage");
        overflow.Should().Throw<MotionException>().Which.Error.Should().Be(MotionError.OutOfRange);
        Words.ToRaw(2_147_483.647, "target").Should().Be(int.MaxValue, "the last representable value still fits");
    }

    [Fact(DisplayName = "GA-U-03 The status block parses")]
    public void Parse_FifteenRegisters_EveryFieldEqualsItsSource()
    {
        ushort[] words =
        [
            3, 0x0063,              // State, Flags
            0x25A0, 0x0026,         // ActualPosition 2 500 000
            0x3CB0, 0xFFFF,         // ActualVelocity -50 000
            4, 42,                  // FaultCode, CommandAck
            0xFF38, 0xFFFF,         // TravelMin -200
            0x9680, 0x0098,         // TravelMax 10 000 000
            0xA120, 0x0007,         // MaxVelocity 500 000
            1,                      // MapVersion
        ];

        var s = StatusBlock.Parse(words);

        s.Should().Be(new StatusBlock(3,
            StatusFlags.Homed | StatusFlags.InPosition | StatusFlags.DriveReady | StatusFlags.Moving,
            2_500_000, -50_000, 4, 42, -200, 10_000_000, 500_000, 1));
        s.Homed.Should().BeTrue();
        s.LimitsPublished.Should().BeTrue();
        s.LimitsValid.Should().BeTrue();

        var shortRead = () => StatusBlock.Parse(words.AsSpan(0, 14));
        shortRead.Should().Throw<ArgumentException>().WithMessage("*15*14*");
    }

    [Fact(DisplayName = "GA-U-04 Bases move whole blocks")]
    public void RegisterMap_RelocatedBases_AddressesFollowAndOverlapIsRefused()
    {
        var map = new RegisterMap(200, 300);
        map.Heartbeat.Should().Be(208);
        map.CommandAck.Should().Be(307);
        map.MapVersion.Should().Be(314);
        map.Invoking(m => m.Validate()).Should().NotThrow();

        RegisterMap.Default.Heartbeat.Should().Be(8);
        RegisterMap.Default.MapVersion.Should().Be(114);

        new RegisterMap(95, 100).Invoking(m => m.Validate()).Should().Throw<ArgumentException>().WithMessage("*overlaps*");
        new RegisterMap(100, 88).Invoking(m => m.Validate()).Should().Throw<ArgumentException>().WithMessage("*overlaps*");
        new RegisterMap(0, 65_530).Invoking(m => m.Validate()).Should().Throw<ArgumentException>().WithMessage("*65535*");
    }

    [Fact(DisplayName = "GA-U-05 The beat and the sequence never write 0")]
    public void NextBeatAndNextSeq_Wrap_SkipZero()
    {
        AxisHeartbeat.NextBeat(65535).Should().Be(1);
        AxisEngine.NextSeq(65535).Should().Be(1);
        AxisHeartbeat.NextBeat(0).Should().Be(1);
        AxisEngine.NextSeq(41).Should().Be(42);
    }
}
