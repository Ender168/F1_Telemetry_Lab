using F1TelemetryLab;
using System.Buffers.Binary;

namespace F1TelemetryLab.Tests;

public sealed class NearbyOverlayTests
{
    private static readonly int[] Slots = { 7, 4, 21, 8, 12 };
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
    private static RaceEngineerService Service() => new(
        new RaceEngineerProfileLoadResult("", Array.Empty<RaceEngineerProfile>(), Array.Empty<string>()),
        new ErsProfileLoadResult("", Array.Empty<ErsControlProfile>(), Array.Empty<string>()));

    [Fact]
    public void FivePositionsFollowRaceOrderAndHighPlayerSlotWithActualTyreAges()
    {
        var service = Service();
        service.ProcessPacket(Laps(1, 1), Start);
        service.ProcessPacket(Status(2, age: 7), Start.AddMilliseconds(20));
        var nearby = service.Snapshot.NearbyCars;
        Assert.Equal(Slots, nearby.Select(x => x.CarIndex));
        Assert.True(nearby[2].IsPlayer);
        Assert.All(nearby, x => Assert.Equal(7, x.TyreAgeLaps));
        service.ProcessPacket(Status(3, age: 0), Start.AddMilliseconds(40));
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Equal(0, x.TyreAgeLaps));
        Assert.Contains("0 кр.", RaceEngineerText.FormatNearbyTyres(service.Snapshot, true, Start.AddMilliseconds(40)));
    }

    [Fact]
    public void NeighboursKeepTheirOwnLastThreeLapsAfterOvertake()
    {
        var service = Service();
        for (var lap = 1; lap <= 5; lap++)
            service.ProcessPacket(Laps(lap, (uint)lap), Start.AddSeconds(lap));
        var ahead = RaceEngineerText.Neighbour(service.Snapshot, -1, Start.AddSeconds(5));
        Assert.NotNull(ahead);
        Assert.Equal(4, ahead.CarIndex);
        Assert.Equal(new[] { 2, 3, 4 }, ahead.LastLaps.Select(x => x.LapNumber));
        Assert.All(ahead.LastLaps, x => Assert.Equal(90_400u, x.LapTimeMs));
        var swapped = Laps(5, 6);
        swapped[F12026Parser.HeaderSize + 21 * 57 + 32] = 2;
        swapped[F12026Parser.HeaderSize + 4 * 57 + 32] = 3;
        service.ProcessPacket(swapped, Start.AddSeconds(6));
        var behind = RaceEngineerText.Neighbour(service.Snapshot, 1, Start.AddSeconds(6));
        Assert.NotNull(behind);
        Assert.Equal(4, behind.CarIndex);
        Assert.Equal(ahead.LastLaps, behind.LastLaps);
        Assert.Equal(7, RaceEngineerText.Neighbour(service.Snapshot, -1, Start.AddSeconds(6))!.CarIndex);
    }

    [Fact]
    public void HistoryBackfillsThreeCompletedLapsButExcludesCurrentLap()
    {
        var service = Service();
        service.ProcessPacket(Laps(10, 1), Start);
        var history = Packet(11, 7 + 100 * 14 + 8 * 3, 2);
        history[F12026Parser.HeaderSize] = 4;
        history[F12026Parser.HeaderSize + 1] = 10;
        for (var lap = 0; lap < 10; lap++)
        {
            var row = history.AsSpan(F12026Parser.HeaderSize + 7 + lap * 14, 14);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)(90_000 + lap));
            row[13] = 15;
        }
        service.ProcessPacket(history, Start.AddMilliseconds(20));
        var ahead = RaceEngineerText.Neighbour(service.Snapshot, -1, Start.AddMilliseconds(20));
        Assert.NotNull(ahead);
        Assert.Equal(new[] { 7, 8, 9 }, ahead.LastLaps.Select(x => x.LapNumber));
        Assert.DoesNotContain("L10", RaceEngineerText.FormatOpponentLaps(ahead, true));
    }

    [Fact]
    public void MissingAndExpiredTyreDataAreUnknownRatherThanZero()
    {
        var service = Service();
        service.ProcessPacket(Laps(1, 1), Start);
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Null(x.TyreAgeLaps));
        Assert.Contains("?", RaceEngineerText.FormatNearbyTyres(service.Snapshot, true, Start));
        service.ProcessPacket(Status(2, 8), Start);
        service.ProcessPacket(Laps(1, 3), Start.AddSeconds(6));
        var text = RaceEngineerText.FormatNearbyTyres(service.Snapshot, true, Start.AddSeconds(6));
        Assert.DoesNotContain("8 кр.", text);
        Assert.Contains("?", text);
        Assert.Null(RaceEngineerText.Neighbour(service.Snapshot, -1, Start.AddSeconds(12)));
        Assert.Equal("Ожидание позиций", RaceEngineerText.FormatNearbyTyres(service.Snapshot, true, Start.AddSeconds(12)));
    }

    [Fact]
    public void SessionChangeAndFlashbackClearOldHistories()
    {
        var service = Service();
        service.ProcessPacket(Laps(1, 1), Start);
        service.ProcessPacket(Laps(2, 2), Start.AddSeconds(1));
        Assert.NotEmpty(service.Snapshot.NearbyCars[1].LastLaps);
        var flashback = Packet(3, 12, 3);
        "FLBK"u8.CopyTo(flashback.AsSpan(F12026Parser.HeaderSize));
        service.ProcessPacket(flashback, Start.AddSeconds(2));
        Assert.Empty(service.Snapshot.NearbyCars);
        service.ProcessPacket(Status(2, 9), Start.AddSeconds(2.5));
        service.ProcessPacket(Laps(1, 4), Start.AddSeconds(3));
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Null(x.TyreAgeLaps));
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Empty(x.LastLaps));
        var next = Laps(2, 5);
        BinaryPrimitives.WriteUInt64LittleEndian(next.AsSpan(7), 999);
        service.ProcessPacket(next, Start.AddSeconds(4));
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Empty(x.LastLaps));
    }

    [Fact]
    public void PitAndInvalidLapsRetainTheirFlagsAndNamesAreAvailable()
    {
        var service = Service();
        var names = Packet(4, 1 + 60 * 24, 1);
        names[F12026Parser.HeaderSize] = 5;
        foreach (var slot in Slots)
            System.Text.Encoding.UTF8.GetBytes($"Driver {slot}").CopyTo(names, F12026Parser.HeaderSize + 1 + slot * 60 + 10);
        service.ProcessPacket(names, Start);
        var lap = Laps(1, 2);
        lap[F12026Parser.HeaderSize + 4 * 57 + 34] = 1;
        lap[F12026Parser.HeaderSize + 8 * 57 + 37] = 1;
        service.ProcessPacket(lap, Start.AddSeconds(1));
        service.ProcessPacket(Laps(2, 3), Start.AddSeconds(2));
        var ahead = RaceEngineerText.Neighbour(service.Snapshot, -1, Start.AddSeconds(2));
        var behind = RaceEngineerText.Neighbour(service.Snapshot, 1, Start.AddSeconds(2));
        Assert.Equal("Driver 4", ahead!.Name);
        Assert.Contains("PIT", RaceEngineerText.FormatOpponentLaps(ahead, true));
        Assert.Contains("INVALID", RaceEngineerText.FormatOpponentLaps(behind, true));
    }

    [Fact]
    public void LeaderHasNoAheadCardAndOnlyTwoFollowingPositions()
    {
        var service = Service();
        var packet = Laps(1, 1);
        packet[F12026Parser.HeaderSize + 21 * 57 + 32] = 1;
        packet[F12026Parser.HeaderSize + 7 * 57 + 32] = 3;
        service.ProcessPacket(packet, Start);
        Assert.Equal(3, service.Snapshot.NearbyCars.Count);
        Assert.Null(RaceEngineerText.Neighbour(service.Snapshot, -1, Start));
        Assert.Equal(4, RaceEngineerText.Neighbour(service.Snapshot, 1, Start)!.CarIndex);
    }

    [Fact]
    public void NegativeAgeSentinelAndLateStatusPacketCannotInventTyreAge()
    {
        var service = Service();
        service.ProcessPacket(Laps(1, 1), Start);
        service.ProcessPacket(Status(4, 255), Start.AddMilliseconds(10));
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Null(x.TyreAgeLaps));
        service.ProcessPacket(Status(5, 3), Start.AddMilliseconds(20));
        service.ProcessPacket(Status(3, 9), Start.AddMilliseconds(30));
        Assert.All(service.Snapshot.NearbyCars, x => Assert.Equal(3, x.TyreAgeLaps));
    }

    private static byte[] Laps(int lap, uint frame)
    {
        var packet = Packet(2, 57 * 24, frame);
        for (var position = 0; position < Slots.Length; position++)
        {
            var slot = Slots[position];
            var row = packet.AsSpan(F12026Parser.HeaderSize + slot * 57, 57);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)(90_000 + slot * 100));
            row[32] = (byte)(position + 1); row[33] = (byte)lap;
            row[44] = 4; row[45] = 2;
        }
        return packet;
    }

    private static byte[] Status(uint frame, byte age)
    {
        var packet = Packet(7, 59 * 24, frame);
        foreach (var slot in Slots)
        {
            var start = F12026Parser.HeaderSize + slot * 59;
            packet[start + 26] = 17; packet[start + 27] = age;
        }
        return packet;
    }

    private static byte[] Packet(byte id, int size, uint frame)
    {
        var packet = new byte[F12026Parser.HeaderSize + size];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26; packet[5] = 1; packet[6] = id;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19), frame);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), frame);
        packet[27] = 21; packet[28] = 255;
        return packet;
    }
}
