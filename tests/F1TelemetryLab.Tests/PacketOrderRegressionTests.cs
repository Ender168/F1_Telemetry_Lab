using System.Buffers.Binary;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    [Fact]
    public void FlashbackPreservesEarlierCompletedLapAndInvalidatesCancelledDatabaseLaps()
    {
        var invalidations = new List<(ulong, int)>();
        var service = new RaceEngineerService(new("", Array.Empty<RaceEngineerProfile>(), Array.Empty<string>()),
            new("", Array.Empty<ErsControlProfile>(), Array.Empty<string>()),
            lapInvalidationSink: (uid, lap) => invalidations.Add((uid, lap)));
        uint frame = 0;
        void Send(byte[] packet, float time)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), ++frame);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(15), time);
            service.ProcessPacket(packet, DateTimeOffset.UnixEpoch.AddSeconds(frame));
        }
        byte[] Lap(byte number)
        {
            var packet = LapPacket(1000);
            packet[F12026Parser.HeaderSize + 33] = number;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(F12026Parser.HeaderSize), 90000);
            return packet;
        }
        Send(SessionPacket(true, 0), 0);
        Send(Lap(1), 0);
        Send(Lap(2), 90);
        Send(Lap(3), 180);
        Assert.Equal(2, service.Snapshot.LastLaps.Count);
        var flashback = FlashbackPacket();
        BinaryPrimitives.WriteSingleLittleEndian(flashback.AsSpan(F12026Parser.HeaderSize + 8), 100);
        Send(flashback, 180);
        Assert.Equal(1, Assert.Single(service.Snapshot.LastLaps).LapNumber);
        Send(Lap(2), 100);
        Assert.NotEmpty(invalidations);
        Assert.All(invalidations, x => Assert.Equal((91UL, 2), x));
    }

    [Fact]
    public void OldThrottleCannotActivateBoostAndPreviousSessionCannotReturn()
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun,
            clock, new FlashbackSink(), new List<ErsAuditRecord>());
        FeedFlashbackState(service, clock, 100, 4500);
        var coasting = TelemetryPacket();
        BinaryPrimitives.WriteSingleLittleEndian(coasting.AsSpan(F12026Parser.HeaderSize + 2), 0);
        SendFrame(service, clock, coasting, 101);
        SendFrame(service, clock, LapPacket(3500), 102);
        Assert.Equal("default", service.LastDecision!.RuleId);
        SendFrame(service, clock, TelemetryPacket(), 99);
        Assert.Equal("default", service.LastDecision!.RuleId);
        foreach (var packet in new[] { SessionPacket(true, 0), LapPacket(4500), TelemetryPacket(), StatusPacket(ErsDeployMode.Medium) })
        {
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 92);
            SendFrame(service, clock, packet, 1);
        }
        var decision = service.LastDecision;
        Assert.NotNull(decision);
        SendFrame(service, clock, SessionPacket(true, 0), 200);
        SendFrame(service, clock, LapPacket(3500), 201);
        Assert.Same(decision, service.LastDecision);
    }

    [Fact]
    public async Task LiveDecisionsContinueWhileSqliteWriterIsLocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "f1_live_disk_" + Guid.NewGuid().ToString("N"));
        await using var recorder = new UdpRecorder();
        try
        {
            int port;
            using (var free = new System.Net.Sockets.UdpClient(0))
                port = ((System.Net.IPEndPoint)free.Client.LocalEndPoint!).Port;
            recorder.Start(port, root, new ErsAutopilotOptions { OperatingMode = ErsAutopilotOperatingMode.DryRun });
            using (var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={recorder.CurrentSession!.DatabasePath};Pooling=False"))
            {
                blocker.Open();
                using var transaction = blocker.BeginTransaction();
                using var sender = new System.Net.Sockets.UdpClient();
                foreach (var packet in new[] { SessionPacket(true, 0), LapPacket(3500), TelemetryPacket(), StatusPacket(ErsDeployMode.Medium) })
                    await sender.SendAsync(packet, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (recorder.ErsDecision is null) await Task.Delay(10, timeout.Token);
                Assert.NotNull(recorder.ErsDecision);
                transaction.Rollback();
            }
            await recorder.DiscardAsync();
            Assert.False(recorder.IsActive);
        }
        finally
        {
            if (recorder.IsActive) await recorder.DiscardAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OlderLapPacketMustNotReplaceCurrentDistance()
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun,
            clock, new FlashbackSink(), new List<ErsAuditRecord>());
        FeedFlashbackState(service, clock, 100, 4500);
        Assert.Equal("default", service.LastDecision!.RuleId);
        clock.Now = clock.Now.AddMilliseconds(10);
        SendFrame(service, clock, LapPacket(3500), 99);
        Assert.Equal("default", service.LastDecision!.RuleId);
    }

    [Fact]
    public void SameLapFlashbackMustNotRetainCancelledInvalidation()
    {
        var service = new RaceEngineerService(
            new RaceEngineerProfileLoadResult("", Array.Empty<RaceEngineerProfile>(), Array.Empty<string>()),
            new ErsProfileLoadResult("", Array.Empty<ErsControlProfile>(), Array.Empty<string>()));
        var now = DateTimeOffset.UnixEpoch;
        void Send(byte[] packet, uint frame)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), frame);
            service.ProcessPacket(packet, now.AddMilliseconds(frame * 10));
        }
        Send(SessionPacket(true, 0), 1);
        Send(LapPacket(1000), 2);
        var invalid = LapPacket(2000);
        invalid[F12026Parser.HeaderSize + 37] = 1;
        Send(invalid, 3);
        Send(FlashbackPacket(), 4);
        Send(LapPacket(1000), 5);
        Send(invalid, 3); // Delayed packet from the cancelled branch.
        var next = LapPacket(0);
        next[F12026Parser.HeaderSize + 33] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(F12026Parser.HeaderSize), 90000);
        Send(next, 6);
        var completed = Assert.Single(service.Snapshot.LastLaps);
        Assert.True(completed.Clean);
        Assert.True(double.IsNaN(completed.TyreWearDeltaPct));
    }
}
