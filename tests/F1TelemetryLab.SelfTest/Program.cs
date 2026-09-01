using System.Buffers.Binary;
using System.Text;
using F1TelemetryLab;
using Microsoft.Data.Sqlite;

var tests = new (string Name, Action Run)[]
{
    ("header carries overall frame", HeaderCarriesOverallFrame),
    ("telemetry carries overall frame", TelemetryCarriesOverallFrame),
    ("lap layout follows 2026 spec", LapLayoutFollows2026Spec),
    ("status layout follows 2026 spec", StatusLayoutFollows2026Spec),
    ("damage layout follows 2026 spec", DamageLayoutFollows2026Spec),
    ("flashback event carries target", FlashbackEventCarriesTarget),
    ("participant layout follows 2026 spec", ParticipantLayoutFollows2026Spec),
    ("final classification layout", FinalClassificationLayout),
    ("completed lap is authoritative", CompletedLapIsAuthoritative),
    ("partial lap cannot become fastest", PartialLapCannotBecomeFastest),
    ("state reset abandons old branch without confirming rewind", StateResetAbandonsOldBranch),
    ("session UIDs stay isolated", SessionUidsStayIsolated),
    ("invalid lap is never clean", InvalidLapIsNeverClean),
    ("pit lap closes the old stint", PitLapClosesOldStint),
    ("one pit stop is not duplicated", OnePitStopIsNotDuplicated),
    ("pit lap cannot become best clean", PitLapCannotBecomeBestClean),
    ("short telemetry gaps are interpolated", ShortTelemetryGapsAreInterpolated),
    ("long telemetry gaps are rejected", LongTelemetryGapsAreRejected),
    ("minor header noise is a warning", MinorHeaderNoiseIsWarning),
    ("queue loss is unreliable", QueueLossIsUnreliable),
    ("analysis isolates the latest logical session", AnalysisIsolatesLatestLogicalSession)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} self-tests passed.");
return failed == 0 ? 0 : 1;

static void HeaderCarriesOverallFrame()
{
    var packet = Packet(packetId: 6, payloadSize: 0, sessionUid: 42, frame: 17, overall: 99, player: 3);
    Check(F12026Parser.TryParseHeader(packet, out var header), "header rejected");
    Equal((ushort)2026, header.PacketFormat, "packet format");
    Equal((ulong)42, header.SessionUid, "session uid");
    Equal((uint)17, header.FrameIdentifier, "frame");
    Equal((uint)99, header.OverallFrameIdentifier, "overall frame");
    Equal((byte)3, header.PlayerCarIndex, "player index");
}

static void TelemetryCarriesOverallFrame()
{
    const int rowSize = 59;
    var packet = Packet(packetId: 6, payloadSize: rowSize * 24, sessionUid: 7, frame: 100, overall: 150, player: 0);
    var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
    BinaryPrimitives.WriteUInt16LittleEndian(row, 321);
    WriteFloat(row, 2, 0.75f);
    WriteFloat(row, 6, -0.25f);
    WriteFloat(row, 10, 0.10f);
    row[15] = 7;
    BinaryPrimitives.WriteUInt16LittleEndian(row[16..], 12_345);
    row[18] = 1;

    var samples = F12026Parser.ParseCarTelemetryPacket(packet, DateTimeOffset.UnixEpoch);
    Check(samples.Count == 24, "expected 24 car slots");
    var first = samples[0];
    Equal((uint)150, first.OverallFrameIdentifier, "overall frame");
    Equal(321, first.Speed, "speed");
    Near(0.75, first.Throttle, 0.0001, "throttle");
    Check(first.IsPlayer, "player flag");
}

static void LapLayoutFollows2026Spec()
{
    var packet = LapPacket(sessionUid: 8, frame: 21, overall: 31, sessionTime: 12.5f, lap: 7, distance: 1_234.5f,
        currentMs: 65_432, lastMs: 90_123, invalid: true);
    var row = packet.AsSpan(F12026Parser.HeaderSize, 57);
    BinaryPrimitives.WriteUInt16LittleEndian(row[8..], 5_678);
    row[10] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(row[11..], 4_321);
    row[13] = 1;
    row[32] = 4;
    row[34] = 2;
    row[35] = 1;
    row[36] = 2;
    row[38] = 5;
    row[39] = 3;
    row[44] = 4;
    row[45] = 2;

    var sample = F12026Parser.ParseLapDataPacket(packet, DateTimeOffset.UnixEpoch)[0];
    Equal((uint)90_123, sample.LastLapTimeMs, "last lap");
    Equal((uint)65_432, sample.CurrentLapTimeMs, "current lap");
    Equal(65_678, sample.Sector1TimeMs, "sector 1 minute part");
    Equal(64_321, sample.Sector2TimeMs, "sector 2 minute part");
    Near(1_234.5, sample.LapDistance, 0.001, "lap distance");
    Equal(4, sample.Position, "position");
    Equal(7, sample.LapNum, "lap number");
    Check(sample.LapInvalid, "invalid flag");
    Equal(3, sample.Warnings, "warnings");
}

static void StatusLayoutFollows2026Spec()
{
    const int rowSize = 59;
    var packet = Packet(packetId: 7, payloadSize: rowSize * 24, sessionUid: 12, frame: 2, overall: 3, player: 0);
    var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
    row[3] = 57;
    WriteFloat(row, 5, 42.5f);
    WriteFloat(row, 13, 18.25f);
    BinaryPrimitives.WriteUInt16LittleEndian(row[23..], 900);
    row[25] = 20;
    row[26] = 18;
    row[27] = 9;
    WriteFloat(row, 29, 600_000f);
    WriteFloat(row, 33, 120_000f);
    WriteFloat(row, 37, 3_500_000f);
    row[41] = 3;
    WriteFloat(row, 42, 800_000f);
    WriteFloat(row, 46, 400_000f);
    WriteFloat(row, 50, 2_000_000f);
    WriteFloat(row, 54, 1_100_000f);

    var sample = F12026Parser.ParseCarStatusPacket(packet, DateTimeOffset.UnixEpoch)[0];
    Equal(57, sample.FrontBrakeBias, "brake bias");
    Near(42.5, sample.FuelInTank, 0.001, "fuel");
    Equal(20, sample.ActualTyreCompound, "actual compound");
    Equal(18, sample.VisualTyreCompound, "visual compound");
    Equal(9, sample.TyresAgeLaps, "tyre age");
    Near(3_500_000, sample.ErsStoreEnergy, 0.1, "ERS store");
    Equal(3, sample.ErsDeployMode, "ERS mode");
}

static void DamageLayoutFollows2026Spec()
{
    const int rowSize = 46;
    var packet = Packet(packetId: 10, payloadSize: rowSize * 24, sessionUid: 13, frame: 2, overall: 3, player: 0);
    var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
    WriteFloat(row, 0, 10f);
    WriteFloat(row, 4, 20f);
    WriteFloat(row, 8, 30f);
    WriteFloat(row, 12, 40f);
    row[16] = 1;
    row[17] = 2;
    row[18] = 3;
    row[19] = 4;
    row[28] = 11;
    row[29] = 12;
    row[30] = 13;
    row[31] = 14;
    row[32] = 15;
    row[33] = 16;

    var sample = F12026Parser.ParseCarDamagePacket(packet, DateTimeOffset.UnixEpoch)[0];
    Near(25, sample.TyreWearAvg, 0.001, "average tyre wear");
    Equal(3, sample.TyreDamageFl, "front-left tyre damage");
    Equal(11, sample.FrontLeftWingDamage, "front-left wing");
    Equal(16, sample.SidepodDamage, "sidepod");
}

static void FlashbackEventCarriesTarget()
{
    var packet = Packet(packetId: 3, payloadSize: 12, sessionUid: 14, frame: 200, overall: 300, player: 0);
    Encoding.ASCII.GetBytes("FLBK", packet.AsSpan(F12026Parser.HeaderSize, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(F12026Parser.HeaderSize + 4), 123);
    WriteFloat(packet, F12026Parser.HeaderSize + 8, 45.5f);

    var sample = F12026Parser.ParseEventPacket(packet, DateTimeOffset.UnixEpoch);
    Check(sample is not null, "flashback event rejected");
    Check(sample!.DetailsJson.Contains("123", StringComparison.Ordinal), "flashback frame missing");
    Check(sample.DetailsJson.Contains("45.5", StringComparison.Ordinal), "flashback time missing");
}

static void FinalClassificationLayout()
{
    const int rowSize = 46;
    var packet = Packet(packetId: 8, payloadSize: 1 + rowSize, sessionUid: 9, frame: 400, overall: 900, player: 0);
    packet[F12026Parser.HeaderSize] = 1;
    var row = packet.AsSpan(F12026Parser.HeaderSize + 1, rowSize);
    row[0] = 1;
    row[1] = 58;
    row[2] = 3;
    row[3] = 25;
    row[4] = 2;
    row[5] = 3;
    row[6] = 4;
    BinaryPrimitives.WriteUInt32LittleEndian(row[7..], 81_234);
    WriteDouble(row, 11, 5_432.125);
    row[19] = 5;
    row[20] = 2;
    row[21] = 3;

    var samples = F12026Parser.ParseFinalClassificationPacket(packet, DateTimeOffset.UnixEpoch);
    Equal(1, samples.Count, "classification row count");
    var sample = samples[0];
    Equal(1, sample.Position, "position");
    Equal(58, sample.NumLaps, "laps");
    Equal((uint)81_234, sample.BestLapTimeMs, "best lap");
    Near(5_432.125, sample.TotalRaceTimeSeconds, 0.00001, "race time");
    Equal(4, sample.ResultReason, "result reason");
}

static void ParticipantLayoutFollows2026Spec()
{
    const int rowSize = 60;
    var packet = Packet(packetId: 4, payloadSize: 1 + rowSize * 24, sessionUid: 11, frame: 20, overall: 25, player: 0);
    packet[F12026Parser.HeaderSize] = 1;
    var row = packet.AsSpan(F12026Parser.HeaderSize + 1, rowSize);
    row[0] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(row[1..], 513);
    BinaryPrimitives.WriteUInt16LittleEndian(row[5..], 1_025);
    row[8] = 44;
    Encoding.UTF8.GetBytes("TEST DRIVER", row[10..42]);
    row[42] = 1;
    row[43] = 1;

    var samples = F12026Parser.ParseParticipantsPacket(packet, DateTimeOffset.UnixEpoch);
    Equal(1, samples.Count, "active participant count");
    var sample = samples[0];
    Equal(513, sample.DriverId, "16-bit driver id");
    Equal(1_025, sample.TeamId, "16-bit team id");
    Equal(44, sample.RaceNumber, "race number");
    Equal("TEST DRIVER", sample.Name, "participant name");
    Equal(1, sample.YourTelemetry, "telemetry setting");
    Equal(1, sample.ShowOnlineNames, "online-name setting");
}

static void CompletedLapIsAuthoritative()
{
    var samples = new[]
    {
        Lap(uid: 1, overall: 100, frame: 10, lap: 1, distance: 0, currentMs: 100, s1: 30_000, s2: 29_000),
        Lap(uid: 1, overall: 101, frame: 11, lap: 1, distance: 950, currentMs: 89_500, s1: 30_000, s2: 29_000),
        Lap(uid: 1, overall: 102, frame: 12, lap: 2, distance: 0, currentMs: 50, lastMs: 90_000)
    };
    var result = LapQualityAnalyzer.Analyze(samples, 1_000, out var rewinds);
    var lap = result.Single(x => x.LapNum == 1);
    Equal(LapState.Complete, lap.State, "lap state");
    Check(lap.CleanLap, "completed lap should be clean");
    Equal((uint)90_000, lap.LapTimeMs, "authoritative lap time");
    Equal(31_000, lap.Sector3TimeMs, "sector 3");
    Equal(0, rewinds.Count, "rewind count");
}

static void PartialLapCannotBecomeFastest()
{
    var samples = new[]
    {
        Lap(uid: 1, overall: 100, frame: 10, lap: 1, distance: 450, currentMs: 20_000),
        Lap(uid: 1, overall: 101, frame: 11, lap: 1, distance: 900, currentMs: 40_000)
    };
    var result = LapQualityAnalyzer.Analyze(samples, 1_000, out _).Single();
    Equal(LapState.PartialStart, result.State, "partial state");
    Equal((uint)0, result.LapTimeMs, "partial time");
    Check(!result.CleanLap, "partial lap marked clean");
}

static void StateResetAbandonsOldBranch()
{
    var samples = new[]
    {
        Lap(uid: 1, overall: 100, frame: 100, lap: 1, distance: 0, currentMs: 100, sessionTime: 1),
        Lap(uid: 1, overall: 101, frame: 101, lap: 1, distance: 950, currentMs: 85_000, sessionTime: 86),
        Lap(uid: 1, overall: 102, frame: 102, lap: 2, distance: 0, currentMs: 50, lastMs: 86_000, sessionTime: 87),
        Lap(uid: 1, overall: 103, frame: 90, lap: 1, distance: 0, currentMs: 20_000, sessionTime: 20),
        Lap(uid: 1, overall: 104, frame: 91, lap: 1, distance: 950, currentMs: 81_500, sessionTime: 82),
        Lap(uid: 1, overall: 105, frame: 92, lap: 2, distance: 0, currentMs: 50, lastMs: 82_000, sessionTime: 83)
    };
    var result = LapQualityAnalyzer.Analyze(
        samples,
        1_000,
        Array.Empty<FlashbackSignal>(),
        out var confirmedRewinds,
        out var suspectedStateResets);
    var lap = result.Single(x => x.LapNum == 1);
    Equal(LapState.Complete, lap.State, "reset branch state");
    Check(lap.CleanLap, "reset-only lap should remain clean");
    Equal((uint)82_000, lap.LapTimeMs, "active branch time");
    Equal((uint)103, lap.ActiveFromOverallFrame, "active branch start");
    Equal(0, confirmedRewinds.Count, "unconfirmed rewind count");
    Check(suspectedStateResets.Count >= 1, "suspected state reset missing");
}

static void SessionUidsStayIsolated()
{
    var samples = new[]
    {
        Lap(uid: 1, overall: 1, frame: 1, lap: 1, distance: 0, currentMs: 1),
        Lap(uid: 2, overall: 1, frame: 1, lap: 1, distance: 0, currentMs: 1)
    };
    var result = LapQualityAnalyzer.Analyze(samples, 1_000, out _);
    Equal(2, result.Count, "session result count");
    Check(result.Select(x => x.SessionUid).Distinct().Count() == 2, "sessions merged");
}

static void InvalidLapIsNeverClean()
{
    var samples = new[]
    {
        Lap(uid: 1, overall: 1, frame: 1, lap: 1, distance: 0, currentMs: 1, invalid: true),
        Lap(uid: 1, overall: 2, frame: 2, lap: 1, distance: 950, currentMs: 80_000, invalid: true),
        Lap(uid: 1, overall: 3, frame: 3, lap: 2, distance: 0, currentMs: 1, lastMs: 81_000)
    };
    var lap = LapQualityAnalyzer.Analyze(samples, 1_000, out _).Single(x => x.LapNum == 1);
    Equal(LapState.Invalid, lap.State, "invalid state");
    Check(!lap.CleanLap, "invalid lap marked clean");
    Equal((uint)81_000, lap.LapTimeMs, "invalid completed lap time retained");
}

static void PitLapClosesOldStint()
{
    var rows = new[]
    {
        RaceLap(lap: 1, visualCompound: 16, tyreAge: 1),
        RaceLap(lap: 2, visualCompound: 16, tyreAge: 2, pit: true),
        RaceLap(lap: 3, visualCompound: 17, tyreAge: 1),
        RaceLap(lap: 4, visualCompound: 17, tyreAge: 2)
    };
    var stints = RaceStrategyAnalyzer.BuildStints(rows);
    Equal(2, stints.Count, "stint count");
    Check(stints[0].Rows.Select(x => x.LapNum).SequenceEqual(new[] { 1, 2 }), "pit lap must close old stint");
    Check(stints[1].Rows.Select(x => x.LapNum).SequenceEqual(new[] { 3, 4 }), "out lap must start new stint");
}

static void OnePitStopIsNotDuplicated()
{
    var rows = new[]
    {
        RaceLap(lap: 1, visualCompound: 16, tyreAge: 5),
        RaceLap(lap: 2, visualCompound: 16, tyreAge: 6, pit: true),
        RaceLap(lap: 3, visualCompound: 17, tyreAge: 1)
    };
    var stops = RaceStrategyAnalyzer.DetectPitStops(rows);
    Equal(1, stops.Count, "pit stop count");
    Equal(2, stops[0].LapNum, "canonical pit lap");
}

static void PitLapCannotBecomeBestClean()
{
    var rows = new[]
    {
        new RaceLapReportRow { LapNum = 1, CleanLap = true, LapTimeMs = 90_000 },
        new RaceLapReportRow { LapNum = 2, CleanLap = true, PitThisLap = true, LapTimeMs = 50_000 }
    };
    var summary = RaceAnalysisDataService.BuildRaceSummary(rows);
    Check(summary.Contains("Best clean 1:30.000", StringComparison.Ordinal), "pit lap selected as best clean");
    Check(summary.Contains("Clean laps 1/2", StringComparison.Ordinal), "pit lap counted as clean pace lap");
}

static void ShortTelemetryGapsAreInterpolated()
{
    var points = new[] { (Distance: 0.0, Value: 100.0), (Distance: 80.0, Value: 180.0) };
    var value = DistanceSeriesInterpolator.Linear(points, 40, p => p.Distance, p => p.Value);
    Check(value is not null, "short gap was rejected");
    Near(140, value ?? double.NaN, 0.0001, "interpolated value");
}

static void LongTelemetryGapsAreRejected()
{
    var points = new[] { (Distance: 0.0, Value: 100.0), (Distance: 120.0, Value: 220.0) };
    var value = DistanceSeriesInterpolator.Linear(points, 60, p => p.Distance, p => p.Value);
    Check(value is null, "long gap was interpolated");
}

static void MinorHeaderNoiseIsWarning()
{
    var quality = new RecordingQualitySnapshot(10_000, 100_000, 1, 0, 0, 0, 0, 0, 0, 0, 0);
    Equal("Usable with warnings", quality.Rating, "quality rating");
}

static void QueueLossIsUnreliable()
{
    var quality = new RecordingQualitySnapshot(10_000, 100_000, 0, 0, 0, 0, 0, 1, 0, 0, 0);
    Equal("Unreliable", quality.Rating, "quality rating");
}

static void AnalysisIsolatesLatestLogicalSession()
{
    var folder = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_selftest_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var databasePath = Path.Combine(folder, "session.sqlite");
    try
    {
        using (var database = new TelemetryDatabase(databasePath))
        {
            database.SaveMetadata(new SessionMetadata
            {
                SessionName = "SelfTest",
                SessionUid = 202,
                TrackLengthMeters = 1_000,
                SessionFolder = folder,
                DatabasePath = databasePath
            });

            AddLapPackets(database, sessionUid: 101, lapTimeMs: 90_000, sequenceOffset: 0);
            AddLapPackets(database, sessionUid: 202, lapTimeMs: 80_000, sequenceOffset: 10);
        }

        var result = AnalysisEngine.AnalyzeSession(folder);
        Equal(6, result.RawPacketsProcessed, "raw packet count");

        using var con = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT session_uid), MIN(session_uid), MIN(lap_time_ms) FROM lap_summary WHERE clean_lap = 1";
        using var reader = cmd.ExecuteReader();
        Check(reader.Read(), "lap summary missing");
        Equal(1L, reader.GetInt64(0), "logical session count");
        Equal("202", reader.GetString(1), "selected session UID");
        Equal(80_000L, reader.GetInt64(2), "selected session best lap");
        using var analysisCommand = con.CreateCommand();
        analysisCommand.CommandText = "SELECT COUNT(*) FROM analysis_runs";
        Check(Convert.ToInt64(analysisCommand.ExecuteScalar()) > 0, "analysis run record missing");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Windows may retain a native SQLite handle briefly after a successful test.
        }
    }
}

static void AddLapPackets(TelemetryDatabase database, ulong sessionUid, uint lapTimeMs, int sequenceOffset)
{
    var packets = new[]
    {
        LapPacket(sessionUid, 10, 100, 1.0f, 1, 0, 100),
        LapPacket(sessionUid, 11, 101, 80.0f, 1, 990, lapTimeMs - 100),
        LapPacket(sessionUid, 12, 102, 81.0f, 2, 0, 50, lapTimeMs)
    };
    for (var i = 0; i < packets.Length; i++)
    {
        Check(F12026Parser.TryParseHeader(packets[i], out var header), "self-test packet header rejected");
        database.InsertRaw(DateTimeOffset.UnixEpoch.AddSeconds(sequenceOffset + i), header, packets[i]);
    }
}

static RaceLapReportRow RaceLap(int lap, int visualCompound, int tyreAge, bool pit = false) => new()
{
    LapNum = lap,
    VisualCompoundStart = visualCompound,
    VisualCompoundEnd = visualCompound,
    ActualCompoundStart = visualCompound,
    ActualCompoundEnd = visualCompound,
    TyreAgeStart = tyreAge,
    TyreAgeEnd = tyreAge,
    PitThisLap = pit,
    CleanLap = !pit,
    LapTimeMs = pit ? 110_000 : 90_000
};

static LapDataSample Lap(
    ulong uid,
    uint overall,
    uint frame,
    int lap,
    float distance,
    uint currentMs,
    uint lastMs = 0,
    int s1 = 0,
    int s2 = 0,
    float sessionTime = 0,
    bool invalid = false)
{
    return new LapDataSample(
        DateTimeOffset.UnixEpoch.AddMilliseconds(overall), uid, sessionTime, frame, overall, 0, 0, true,
        lastMs, currentMs, s1, s2, 0, 0, distance, distance, 1, lap, 0, 0, 0, invalid, 0, 0, 1, 2);
}

static byte[] Packet(byte packetId, int payloadSize, ulong sessionUid, uint frame, uint overall, byte player)
{
    var bytes = new byte[F12026Parser.HeaderSize + payloadSize];
    var span = bytes.AsSpan();
    BinaryPrimitives.WriteUInt16LittleEndian(span, 2026);
    span[2] = 26;
    span[3] = 1;
    span[4] = 0;
    span[5] = 1;
    span[6] = packetId;
    BinaryPrimitives.WriteUInt64LittleEndian(span[7..], sessionUid);
    WriteFloat(span, 15, 1.25f);
    BinaryPrimitives.WriteUInt32LittleEndian(span[19..], frame);
    BinaryPrimitives.WriteUInt32LittleEndian(span[23..], overall);
    span[27] = player;
    span[28] = 255;
    return bytes;
}

static byte[] LapPacket(
    ulong sessionUid,
    uint frame,
    uint overall,
    float sessionTime,
    int lap,
    float distance,
    uint currentMs,
    uint lastMs = 0,
    bool invalid = false)
{
    const int rowSize = 57;
    var packet = Packet(2, rowSize * 24 + 2, sessionUid, frame, overall, 0);
    WriteFloat(packet, 15, sessionTime);
    var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
    BinaryPrimitives.WriteUInt32LittleEndian(row, lastMs);
    BinaryPrimitives.WriteUInt32LittleEndian(row[4..], currentMs);
    WriteFloat(row, 20, distance);
    WriteFloat(row, 24, Math.Max(0, lap - 1) * 1_000 + distance);
    row[32] = 1;
    row[33] = (byte)lap;
    row[37] = invalid ? (byte)1 : (byte)0;
    row[44] = 4;
    row[45] = 2;
    return packet;
}

static void WriteFloat(Span<byte> span, int offset, float value) =>
    BinaryPrimitives.WriteInt32LittleEndian(span[offset..], BitConverter.SingleToInt32Bits(value));

static void WriteDouble(Span<byte> span, int offset, double value) =>
    BinaryPrimitives.WriteInt64LittleEndian(span[offset..], BitConverter.DoubleToInt64Bits(value));

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string name) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

static void Near(double expected, double actual, double tolerance, string name)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}
