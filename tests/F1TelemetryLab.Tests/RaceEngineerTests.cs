using F1TelemetryLab;
using System.Buffers.Binary;

namespace F1TelemetryLab.Tests;

public sealed class RaceEngineerTests
{
    [Fact]
    public void OverlayLayoutProvidesIndependentMovableWidgets()
    {
        var layout = OverlayLayoutService.Default(1920, 1080);

        Assert.Equal(6, layout.Widgets.Count);
        Assert.Equal(6, layout.Widgets.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(layout.Widgets, x => x.Id == "ers-energy");
        Assert.Contains(layout.Widgets, x => x.Id == "ers-tactical");
        Assert.Contains(layout.Widgets, x => x.Id == "ers-action");
    }

    [Fact]
    public void TyreSetsParserReadsFittedSetAndUsableLife()
    {
        const int setSize = 10;
        const int setCount = 20;
        var packet = Packet(12, 1 + setSize * setCount + 1);
        var payload = packet.AsSpan(F12026Parser.HeaderSize);
        payload[0] = 0;
        var fitted = payload.Slice(1 + 4 * setSize, setSize);
        fitted[0] = 12;
        fitted[1] = 17;
        fitted[2] = 23;
        fitted[3] = 1;
        fitted[5] = 28;
        fitted[6] = 24;
        BinaryPrimitives.WriteInt16LittleEndian(fitted[7..], -120);
        fitted[9] = 1;
        payload[^1] = 4;

        var parsed = F12026Parser.ParseTyreSetsPacket(packet, DateTimeOffset.UtcNow);

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsPlayer);
        Assert.Equal(4, parsed.FittedIndex);
        Assert.Equal(24, parsed.Sets[4].UsableLifeLaps);
        Assert.Equal(-120, parsed.Sets[4].LapDeltaTimeMs);
        Assert.True(parsed.Sets[4].Fitted);
    }

    [Fact]
    public void LiveEngineerUsesOnlyCompletedLapsAndPublishesRangesWithConfidence()
    {
        var profile = new RaceEngineerProfile
        {
            ProfileId = "china-test",
            TrackId = 2,
            TrackName = "China",
            TrackLengthM = 5441,
            SessionTypes = new List<int> { 15 },
            SafeTyreWearPct = 75,
            PitLossGreenSeconds = 23,
            PitLossUncertaintySeconds = 1.8,
            TyreWearPriors = new List<TyreWearPrior> { new() { VisualCompound = 17, WearPctPerLap = 1.8 } },
            ErsEnergyBands = new List<ErsEnergyBand>
            {
                new() { Segment = "T1 -> T4", StartM = 0, EndM = 5441, TargetMinPct = 35, TargetMaxPct = 65 }
            }
        };
        var ersProfile = new ErsControlProfile
        {
            ProfileId = "china-ers-test",
            TrackId = 2,
            TrackLengthM = 5441,
            SessionTypes = new List<int> { 15 },
            Rules = new List<ErsControlRule>
            {
                new() { Id = "boost", Segment = "T13 -> T14", StartM = 3420, EndM = 4350, TargetMode = ErsDeployMode.Boost }
            }
        };
        var completed = new List<CompletedLiveLap>();
        var service = new RaceEngineerService(
            new RaceEngineerProfileLoadResult("", new[] { profile }, Array.Empty<string>()),
            new ErsProfileLoadResult("", new[] { ersProfile }, Array.Empty<string>()),
            completed.Add);
        var now = DateTimeOffset.UtcNow;

        service.ProcessPacket(SessionPacket(), now);
        service.ProcessPacket(StatusPacket(60, tyreAge: 0), now.AddMilliseconds(10));
        service.ProcessPacket(DamagePacket(10), now.AddMilliseconds(20));
        service.ProcessPacket(LapPacket(1, 0), now.AddMilliseconds(30));

        service.ProcessPacket(StatusPacket(55, tyreAge: 1), now.AddSeconds(1));
        service.ProcessPacket(DamagePacket(12), now.AddSeconds(1).AddMilliseconds(10));
        service.ProcessPacket(LapPacket(2, 90_000), now.AddSeconds(1).AddMilliseconds(20));

        service.ProcessPacket(StatusPacket(50, tyreAge: 2), now.AddSeconds(2));
        service.ProcessPacket(DamagePacket(14), now.AddSeconds(2).AddMilliseconds(10));
        service.ProcessPacket(LapPacket(3, 91_000), now.AddSeconds(2).AddMilliseconds(20));

        service.ProcessPacket(StatusPacket(75, tyreAge: 3), now.AddSeconds(3));
        service.ProcessPacket(DamagePacket(16), now.AddSeconds(3).AddMilliseconds(10));
        service.ProcessPacket(LapPacket(4, 92_000), now.AddSeconds(3).AddMilliseconds(20));

        var snapshot = service.Snapshot;
        Assert.Equal(new[] { 1, 2, 3 }, snapshot.LastLaps.Select(x => x.LapNumber));
        Assert.Equal(3, completed.Count);
        Assert.All(snapshot.LastLaps, lap => Assert.True(lap.Clean));
        Assert.True(snapshot.Tyres.Available);
        Assert.Equal(2, snapshot.Tyres.WearRatePctPerLap!.Value, precision: 2);
        Assert.NotNull(snapshot.Tyres.RemainingLapsLow);
        Assert.NotNull(snapshot.Tyres.RemainingLapsHigh);
        Assert.True(snapshot.Tyres.RemainingLapsLow <= snapshot.Tyres.RemainingLapsHigh);
        Assert.Equal(AdviceConfidence.Medium, snapshot.Tyres.Confidence);
        Assert.True(snapshot.Pit.Available);
        Assert.True(snapshot.Pit.PositionLow <= snapshot.Pit.PositionHigh);
        Assert.Equal(ErsAggressionAdvice.Aggressive, snapshot.Ers.Aggression);
        Assert.Equal("T13 -> T14", snapshot.Ers.NextBoostSegment);
    }

    [Fact]
    public void TextMarksEstimatesAndDoesNotPresentThemAsExactValues()
    {
        var display = RaceEngineerText.Format(new RaceEngineerSnapshot(
            DateTimeOffset.UtcNow,
            "test",
            8,
            5,
            Array.Empty<CompletedLiveLap>(),
            new TyreLifeAdvice(true, 17, 5, 20, 21, 19, 20, "FR", 21, 1.8, 24, 32, 75, 3, AdviceConfidence.Medium, "observed"),
            new PitPositionAdvice(true, 8, 10, 23, 1.8, 2, AdviceConfidence.Medium, "live gaps"),
            new ErsRaceAdvice(true, 72, 2, 35, 65, ErsAggressionAdvice.Aggressive, 1, "T10 -> T13", "T13 -> T14", 450, AdviceConfidence.High, "surplus")), true);

        Assert.Contains("~24-32", display.Tyres);
        Assert.Contains("P8-P10", display.Pit);
        Assert.Contains("± 1.8", display.Pit);
        Assert.Contains("T13 -> T14", display.Ers);
        Assert.Contains("среднее", display.Confidence);
    }

    private static byte[] SessionPacket()
    {
        var packet = Packet(1, 662);
        var payload = packet.AsSpan(F12026Parser.HeaderSize);
        payload[3] = 56;
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], 5441);
        payload[6] = 15;
        payload[7] = 2;
        payload[8] = 0;
        payload[124] = 0;
        payload[125] = 0;
        payload[661] = 0;
        return packet;
    }

    private static byte[] StatusPacket(float batteryPct, byte tyreAge)
    {
        const int rowSize = 59;
        var packet = Packet(7, rowSize * F12026Parser.MaxCars2026);
        var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
        row[25] = 12;
        row[26] = 17;
        row[27] = tyreAge;
        BinaryPrimitives.WriteSingleLittleEndian(row[37..], batteryPct / 100f * 4_000_000f);
        row[41] = (byte)ErsDeployMode.Medium;
        return packet;
    }

    private static byte[] DamagePacket(float baseWear)
    {
        const int rowSize = 46;
        var packet = Packet(10, rowSize * F12026Parser.MaxCars2026);
        var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
        BinaryPrimitives.WriteSingleLittleEndian(row[0..], baseWear);
        BinaryPrimitives.WriteSingleLittleEndian(row[4..], baseWear + 0.4f);
        BinaryPrimitives.WriteSingleLittleEndian(row[8..], baseWear + 0.2f);
        BinaryPrimitives.WriteSingleLittleEndian(row[12..], baseWear + 1f);
        return packet;
    }

    private static byte[] LapPacket(byte lapNumber, uint lastLapMs)
    {
        const int rowSize = 57;
        var packet = Packet(2, rowSize * F12026Parser.MaxCars2026);
        var positions = new[] { 3, 1, 2, 4, 5, 6 };
        var leaderGaps = new[] { 20_000, 0, 10_000, 25_000, 30_000, 50_000 };
        for (var car = 0; car < positions.Length; car++)
        {
            var row = packet.AsSpan(F12026Parser.HeaderSize + car * rowSize, rowSize);
            if (car == 0) BinaryPrimitives.WriteUInt32LittleEndian(row[0..], lastLapMs);
            BinaryPrimitives.WriteUInt16LittleEndian(row[14..], car == 0 ? (ushort)1_000 : (ushort)5_000);
            BinaryPrimitives.WriteUInt16LittleEndian(row[17..], checked((ushort)Math.Min(leaderGaps[car], ushort.MaxValue)));
            BinaryPrimitives.WriteSingleLittleEndian(row[20..], 1_000f);
            row[32] = checked((byte)positions[car]);
            row[33] = lapNumber;
            row[44] = 4;
            row[45] = 2;
        }
        return packet;
    }

    private static byte[] Packet(byte packetId, int payloadSize)
    {
        var packet = new byte[F12026Parser.HeaderSize + payloadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26;
        packet[3] = 1;
        packet[5] = 1;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 77);
        packet[27] = 0;
        packet[28] = 255;
        return packet;
    }
}
