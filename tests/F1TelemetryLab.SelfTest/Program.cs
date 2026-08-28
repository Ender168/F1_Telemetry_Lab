using System.Buffers.Binary;
using System.Text;
using F1TelemetryLab;

var tests = new (string Name, Action Run)[]
{
    ("header carries overall frame", HeaderCarriesOverallFrame),
    ("telemetry carries overall frame", TelemetryCarriesOverallFrame),
    ("participant layout follows 2026 spec", ParticipantLayoutFollows2026Spec),
    ("final classification layout", FinalClassificationLayout),
    ("completed lap is authoritative", CompletedLapIsAuthoritative),
    ("partial lap cannot become fastest", PartialLapCannotBecomeFastest),
    ("flashback abandons old branch", FlashbackAbandonsOldBranch),
    ("session UIDs stay isolated", SessionUidsStayIsolated),
    ("invalid lap is never clean", InvalidLapIsNeverClean),
    ("pit lap closes the old stint", PitLapClosesOldStint),
    ("one pit stop is not duplicated", OnePitStopIsNotDuplicated),
    ("pit lap cannot become best clean", PitLapCannotBecomeBestClean),
    ("short telemetry gaps are interpolated", ShortTelemetryGapsAreInterpolated),
    ("long telemetry gaps are rejected", LongTelemetryGapsAreRejected)
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

static void FlashbackAbandonsOldBranch()
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
    var result = LapQualityAnalyzer.Analyze(samples, 1_000, out var rewinds);
    var lap = result.Single(x => x.LapNum == 1);
    Equal(LapState.Rewound, lap.State, "rewound state");
    Check(!lap.CleanLap, "rewound lap marked clean");
    Equal((uint)82_000, lap.LapTimeMs, "active branch time");
    Equal((uint)103, lap.ActiveFromOverallFrame, "active branch start");
    Check(rewinds.Count >= 1, "rewind event missing");
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
    Near(140, value.Value, 0.0001, "interpolated value");
}

static void LongTelemetryGapsAreRejected()
{
    var points = new[] { (Distance: 0.0, Value: 100.0), (Distance: 120.0, Value: 220.0) };
    var value = DistanceSeriesInterpolator.Linear(points, 60, p => p.Distance, p => p.Value);
    Check(value is null, "long gap was interpolated");
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
