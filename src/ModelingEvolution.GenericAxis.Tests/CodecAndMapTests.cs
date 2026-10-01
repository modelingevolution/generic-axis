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

    [Fact(DisplayName = "GA-U-04 Bases move whole blocks, each in its own address space")]
    public void RegisterMap_RelocatedBases_AddressesFollowPerSpaceAndOnlyThe65535BoundIsRefused()
    {
        var map = new RegisterMap(200, 300);
        map.Heartbeat.Should().Be(208);
        map.CommandAck.Should().Be(307);
        map.MapVersion.Should().Be(314);
        map.Describe(RegisterField.Heartbeat).Should().Be("C+8 = holding 208");
        map.Describe(RegisterField.CommandAck).Should().Be("S+7 = input 307");
        map.Describe(RegisterField.MapVersion).Should().Be("S+14 = input 314");
        map.Invoking(m => m.Validate()).Should().NotThrow();

        RegisterMap.Default.Should().Be(new RegisterMap(0, 0), "protocol defaults: holding base 0, input base 0 (ADR-36)");
        RegisterMap.Default.Validate();

        // ADR-36: holding and input registers are separate spaces — equal or interleaved bases are not an overlap.
        new RegisterMap(0, 0).Invoking(m => m.Validate()).Should().NotThrow("the blocks are in separate address spaces");
        new RegisterMap(95, 100).Invoking(m => m.Validate()).Should().NotThrow();

        new RegisterMap(0, 65_530).Invoking(m => m.Validate()).Should().Throw<ArgumentException>()
            .WithMessage("*status block*65535*");
        new RegisterMap(65_530, 0).Invoking(m => m.Validate()).Should().Throw<ArgumentException>()
            .WithMessage("*command block*65535*");
        new RegisterMap(0, 65_521).Invoking(m => m.Validate()).Should().NotThrow("S+14 = input 65535 is the last register");
        new RegisterMap(65_524, 0).Invoking(m => m.Validate()).Should().NotThrow("C+11 = holding 65535 is the last register");
    }

    [Fact(DisplayName = "GA-U-137 An address renders with its space: C+n = holding a, S+n = input a, even at equal bases")]
    public void Describe_BothBasesZero_SameAbsoluteAddressRendersByItsSpace()
    {
        var map = new RegisterMap(0, 0);
        map.Address(RegisterField.FaultCode).Should().Be(map.Address(RegisterField.Acceleration));
        map.Describe(RegisterField.FaultCode).Should().Be("S+6 = input 6");
        map.Describe(RegisterField.Acceleration).Should().Be("C+6 = holding 6");
        map.Describe(RegisterField.WatchdogTrips).Should().Be("C+11 = holding 11", "WatchdogTrips stays a holding register");
        map.DescribeRange(RegisterSpace.Input, 0, RegisterMap.StatusLength).Should().Be("S+0…S+14 = input 0…14");
        map.DescribeRange(RegisterSpace.Holding, 9, 3).Should().Be("C+9…C+11 = holding 9…11");
        map.DescribeRange(RegisterSpace.Holding, 500, 2).Should().Be("holding 500…501", "outside the block: no offset is claimed");
        map.DescribeRange(RegisterSpace.Input, 14, 2).Should().Be("input 14…15", "a range running past the block claims no offset");

        foreach (var f in Fields())
            map.Describe(f).Should().Be(f.Space == RegisterSpace.Holding
                ? $"C+{f.Offset} = holding {f.Offset}" : $"S+{f.Offset} = input {f.Offset}", f.Name);
    }

    [Fact(DisplayName = "GA-U-138 Every field is declared once with the protocol's space and offset")]
    public void RegisterField_Table_MatchesTheMapProperties()
    {
        var map = new RegisterMap(200, 300);
        var fields = Fields().ToList();
        fields.Should().HaveCount(19, "12 command registers as 9 fields, 15 status registers as 10 fields");
        fields.Select(f => f.Name).Should().OnlyHaveUniqueItems();
        foreach (var f in fields)
        {
            var property = typeof(RegisterMap).GetProperty(f.Name)!;
            ((ushort)property.GetValue(map)!).Should().Be(map.Address(f), f.Name);
        }

        fields.Where(f => f.Space == RegisterSpace.Holding).Select(f => f.Offset).Should().OnlyContain(o => o < RegisterMap.CommandLength);
        fields.Where(f => f.Space == RegisterSpace.Input).Select(f => f.Offset).Should().OnlyContain(o => o < RegisterMap.StatusLength);
    }

    private static IEnumerable<RegisterField> Fields() =>
        typeof(RegisterField).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(RegisterField))
            .Select(f => (RegisterField)f.GetValue(null)!);

    [Fact(DisplayName = "GA-U-05 The beat and the sequence never write 0")]
    public void NextBeatAndNextSeq_Wrap_SkipZero()
    {
        AxisHeartbeat.NextBeat(65535).Should().Be(1);
        AxisEngine.NextSeq(65535).Should().Be(1);
        AxisHeartbeat.NextBeat(0).Should().Be(1);
        AxisEngine.NextSeq(41).Should().Be(42);
    }
}
