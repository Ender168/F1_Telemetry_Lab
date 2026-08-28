using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace F1TelemetryLab;

public static class F12026Parser
{
    public const int HeaderSize = 29;
    public const int MaxCars2026 = 24;

    private const int CarTelemetrySize2026 = 59;
    private const int LapDataSize2026 = 57;
    private const int MotionSize2026 = 54;
    private const int CarStatusSize2026 = 59;
    private const int CarDamageSize2026 = 46;
    private const int ParticipantSize2026 = 60;
    private const int FinalClassificationSize2026 = 46;

    public static bool TryParseHeader(ReadOnlySpan<byte> data, out PacketHeader header)
    {
        header = default!;
        if (data.Length < HeaderSize) return false;

        try
        {
            header = new PacketHeader(
                PacketFormat: U16(data, 0),
                GameYear: data[2],
                GameMajorVersion: data[3],
                GameMinorVersion: data[4],
                PacketVersion: data[5],
                PacketId: data[6],
                SessionUid: U64(data, 7),
                SessionTime: F32(data, 15),
                FrameIdentifier: U32(data, 19),
                OverallFrameIdentifier: U32(data, 23),
                PlayerCarIndex: data[27],
                SecondaryPlayerCarIndex: data[28]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<CarTelemetrySample> ParseCarTelemetryPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<CarTelemetrySample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != 2026 || h.PacketId != 6) return samples;

        var offset = HeaderSize;
        for (var i = 0; i < MaxCars2026; i++)
        {
            if (offset + CarTelemetrySize2026 > data.Length) break;
            var c = data.Slice(offset, CarTelemetrySize2026);
            var speed = U16(c, 0);
            var throttle = F32(c, 2);
            var steer = F32(c, 6);
            var brake = F32(c, 10);
            var gear = unchecked((sbyte)c[15]);
            var rpm = U16(c, 16);
            var drs = c[18];

            if (speed <= 450 && throttle >= -0.01f && throttle <= 1.01f && brake >= -0.01f && brake <= 1.01f && steer >= -1.1f && steer <= 1.1f)
            {
                samples.Add(new CarTelemetrySample(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, h.PlayerCarIndex, i,
                    h.PlayerCarIndex == i, speed, throttle, brake, steer, gear, rpm, drs));
            }
            offset += CarTelemetrySize2026;
        }

        return samples;
    }

    public static List<LapDataSample> ParseLapDataPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<LapDataSample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != 2026 || h.PacketId != 2) return samples;

        var offset = HeaderSize;
        for (var i = 0; i < MaxCars2026; i++)
        {
            if (offset + LapDataSize2026 > data.Length) break;
            var c = data.Slice(offset, LapDataSize2026);
            var p = 0;
            var lastLap = U32(c, p); p += 4;
            var currentLap = U32(c, p); p += 4;
            var s1ms = U16(c, p); p += 2; var s1min = c[p++];
            var s2ms = U16(c, p); p += 2; var s2min = c[p++];
            var frontMs = U16(c, p); p += 2; var frontMin = c[p++];
            var leaderMs = U16(c, p); p += 2; var leaderMin = c[p++];
            var lapDistance = F32(c, p); p += 4;
            var totalDistance = F32(c, p); p += 4;
            p += 4; // safety car delta
            var position = c[p++];
            var lapNum = c[p++];
            var pitStatus = c[p++];
            var numPitStops = c[p++];
            var sector = c[p++];
            var lapInvalid = c[p++] != 0;
            var penalties = c[p++];
            var warnings = c[p++];
            p += 4; // corner cutting, unserved penalties, grid position
            var driverStatus = c[p++];
            var resultStatus = c[p++];
            p += 1; // pit lane timer active
            p += 2; // pit lane time ms
            p += 2; // pit stop timer ms
            p += 1; // should serve pen
            // speed trap fields are not required for v0.2 summary

            samples.Add(new LapDataSample(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, h.PlayerCarIndex, i,
                h.PlayerCarIndex == i, lastLap, currentLap, SectorMs(s1ms, s1min), SectorMs(s2ms, s2min), SectorMs(frontMs, frontMin), SectorMs(leaderMs, leaderMin),
                lapDistance, totalDistance, position, lapNum, pitStatus, numPitStops, sector, lapInvalid, penalties, warnings, driverStatus, resultStatus));

            offset += LapDataSize2026;
        }

        return samples;
    }

    public static List<MotionSample> ParseMotionPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<MotionSample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != 2026 || h.PacketId != 0) return samples;

        var offset = HeaderSize;
        for (var i = 0; i < MaxCars2026; i++)
        {
            if (offset + MotionSize2026 > data.Length) break;
            var c = data.Slice(offset, MotionSize2026);
            var x = F32(c, 0); var y = F32(c, 4); var z = F32(c, 8);
            var vx = F32(c, 12); var vy = F32(c, 16); var vz = F32(c, 20);
            var gLat = I16(c, 36) / 1000f;
            var gLong = I16(c, 38) / 1000f;
            var gVert = I16(c, 40) / 1000f;
            var yaw = F32(c, 42); var pitch = F32(c, 46); var roll = F32(c, 50);
            samples.Add(new MotionSample(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, h.PlayerCarIndex, i, h.PlayerCarIndex == i,
                x, y, z, vx, vy, vz, gLat, gLong, gVert, yaw, pitch, roll));
            offset += MotionSize2026;
        }
        return samples;
    }

    public static List<CarStatusSample> ParseCarStatusPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<CarStatusSample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != 2026 || h.PacketId != 7) return samples;

        var offset = HeaderSize;
        for (var i = 0; i < MaxCars2026; i++)
        {
            if (offset + CarStatusSize2026 > data.Length) break;
            var c = data.Slice(offset, CarStatusSize2026);
            var frontBrakeBias = c[3];
            var fuelInTank = F32(c, 5);
            var fuelRemainingLaps = F32(c, 13);
            // F1 25 2026 Season Pack CarStatusData includes uint16 m_drsActivationDistance at bytes 23-24.
            // Everything after it starts at byte 25. Reading from byte 24 turns ERS into comedy-grade nonsense.
            var actualTyreCompound = c[25];
            var visualTyreCompound = c[26];
            var tyreAge = unchecked((sbyte)c[27]);
            var ice = F32(c, 29);
            var mguk = F32(c, 33);
            var ers = F32(c, 37);
            var ersMode = c[41];
            var harvestMguk = F32(c, 42);
            var harvestMguh = F32(c, 46);
            var harvestLimit = F32(c, 50);
            var deployed = F32(c, 54);
            samples.Add(new CarStatusSample(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, h.PlayerCarIndex, i, h.PlayerCarIndex == i,
                frontBrakeBias, fuelInTank, fuelRemainingLaps, actualTyreCompound, visualTyreCompound, tyreAge, ice, mguk, ers, ersMode, harvestMguk, harvestMguh, harvestLimit, deployed));
            offset += CarStatusSize2026;
        }
        return samples;
    }

    public static List<CarDamageSample> ParseCarDamagePacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<CarDamageSample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != 2026 || h.PacketId != 10) return samples;

        var offset = HeaderSize;
        for (var i = 0; i < MaxCars2026; i++)
        {
            if (offset + CarDamageSize2026 > data.Length) break;
            var c = data.Slice(offset, CarDamageSize2026);
            var rl = F32(c, 0); var rr = F32(c, 4); var fl = F32(c, 8); var fr = F32(c, 12);
            var avg = (rl + rr + fl + fr) / 4f;
            var tyreDmgRl = c[16]; var tyreDmgRr = c[17]; var tyreDmgFl = c[18]; var tyreDmgFr = c[19];
            var wingFl = c[28]; var wingFr = c[29]; var rearWing = c[30]; var floor = c[31]; var diffuser = c[32]; var sidepod = c[33];
            samples.Add(new CarDamageSample(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, h.PlayerCarIndex, i, h.PlayerCarIndex == i,
                rl, rr, fl, fr, avg, tyreDmgRl, tyreDmgRr, tyreDmgFl, tyreDmgFr, wingFl, wingFr, rearWing, floor, diffuser, sidepod));
            offset += CarDamageSize2026;
        }
        return samples;
    }

    public static EventSample? ParseEventPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        if (!TryParseHeader(data, out var h)) return null;
        if (h.PacketFormat != 2026 || h.PacketId != 3) return null;
        if (data.Length < HeaderSize + 4) return null;

        var code = Encoding.ASCII.GetString(data.Slice(HeaderSize, 4));
        var name = EventNames.TryGetValue(code, out var eventName) ? eventName : code;
        var offset = HeaderSize + 4;
        var vehicle = -1;
        var other = -1;
        var details = new Dictionary<string, object?>();
        try
        {
            if (code == "FTLP" && offset + 5 <= data.Length)
            {
                vehicle = data[offset];
                details["vehicle_idx"] = vehicle;
                details["lap_time"] = F32(data, offset + 1);
            }
            else if (code == "RTMT" && offset + 2 <= data.Length)
            {
                vehicle = data[offset];
                details["vehicle_idx"] = vehicle;
                details["reason"] = data[offset + 1];
            }
            else if (code == "PENA" && offset + 7 <= data.Length)
            {
                vehicle = data[offset + 2];
                other = data[offset + 3];
                details["penalty_type"] = data[offset];
                details["infringement_type"] = data[offset + 1];
                details["vehicle_idx"] = vehicle;
                details["other_vehicle_idx"] = other;
                details["time"] = data[offset + 4];
                details["lap_num"] = data[offset + 5];
                details["places_gained"] = data[offset + 6];
            }
            else if (code == "OVTK" && offset + 2 <= data.Length)
            {
                vehicle = data[offset];
                other = data[offset + 1];
                details["overtaking_vehicle_idx"] = vehicle;
                details["being_overtaken_vehicle_idx"] = other;
            }
            else if (code == "COLL" && offset + 2 <= data.Length)
            {
                vehicle = data[offset];
                other = data[offset + 1];
                details["vehicle1_idx"] = vehicle;
                details["vehicle2_idx"] = other;
                if (offset + 3 <= data.Length) details["severity"] = data[offset + 2];
            }
            else if (code == "FLBK" && offset + 8 <= data.Length)
            {
                details["flashback_frame_identifier"] = U32(data, offset);
                details["flashback_session_time"] = F32(data, offset + 4);
            }
        }
        catch
        {
            details["warning"] = "event details parse failed";
        }

        var json = JsonSerializer.Serialize(details);
        return new EventSample(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, h.PlayerCarIndex, code, name, vehicle, other, json);
    }

    public static List<ParticipantSample> ParseParticipantsPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<ParticipantSample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != 2026 || h.PacketId != 4) return samples;

        if (data.Length <= HeaderSize) return samples;
        var activeCars = data[HeaderSize];
        var offset = HeaderSize + 1;
        var available = Math.Max(0, (data.Length - offset) / ParticipantSize2026);
        var count = Math.Clamp(Math.Min(activeCars, available), 0, MaxCars2026);

        for (var i = 0; i < count; i++)
        {
            if (offset + ParticipantSize2026 > data.Length) break;
            var c = data.Slice(offset, ParticipantSize2026);
            var driverId = U16(c, 1);
            var teamId = U16(c, 5);
            var name = DecodeName(c.Slice(10, 32));
            samples.Add(new ParticipantSample(
                receivedAt,
                h.SessionUid,
                h.SessionTime,
                h.FrameIdentifier,
                h.OverallFrameIdentifier,
                i,
                c[0],
                driverId,
                teamId,
                c[8],
                name,
                c[42],
                c[43]));
            offset += ParticipantSize2026;
        }
        return samples;
    }


    public static ParticipantPacketDebug? ParseParticipantsDebug(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        if (!TryParseHeader(data, out var h)) return null;
        if (h.PacketFormat != 2026 || h.PacketId != 4) return null;
        var active = data.Length > HeaderSize ? data[HeaderSize] : 0;
        var payload = Math.Max(0, data.Length - HeaderSize - 1);
        var rows60 = payload / 60;
        var rows58 = payload / 58;
        var firstNames = new List<string>();
        var offset = HeaderSize + 1;
        for (var i = 0; i < Math.Min(rows60, 8); i++)
        {
            if (offset + 60 > data.Length) break;
            var raw = data.Slice(offset + 10, 32);
            firstNames.Add(DecodeName(raw));
            offset += 60;
        }
        return new ParticipantPacketDebug(receivedAt, h.SessionUid, h.SessionTime, h.FrameIdentifier, h.OverallFrameIdentifier, data.Length, active, rows60, rows58, string.Join(" | ", firstNames));
    }

    public static List<FinalClassificationSample> ParseFinalClassificationPacket(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var samples = new List<FinalClassificationSample>(MaxCars2026);
        if (!TryParseHeader(data, out var h)) return samples;
        if (h.PacketFormat != AppInfo.SupportedPacketFormat || h.PacketId != 8) return samples;
        if (data.Length <= HeaderSize) return samples;

        var numCars = Math.Min(data[HeaderSize], (byte)MaxCars2026);
        var offset = HeaderSize + 1;
        for (var i = 0; i < numCars; i++)
        {
            if (offset + FinalClassificationSize2026 > data.Length) break;
            var c = data.Slice(offset, FinalClassificationSize2026);
            samples.Add(new FinalClassificationSample(
                receivedAt,
                h.SessionUid,
                h.SessionTime,
                h.FrameIdentifier,
                h.OverallFrameIdentifier,
                i,
                h.PlayerCarIndex == i,
                c[0],
                c[1],
                c[2],
                c[3],
                c[4],
                c[5],
                U32(c, 7),
                F64(c, 11),
                c[19],
                c[20],
                c[21],
                c[6]));
            offset += FinalClassificationSize2026;
        }

        return samples;
    }

    public static SessionMetadata? TryParseSessionMetadata(ReadOnlySpan<byte> data, DateTimeOffset startedAt)
    {
        if (!TryParseHeader(data, out var h)) return null;
        if (h.PacketFormat != 2026 || h.PacketId != 1) return null;
        if (data.Length < HeaderSize + 8) return null;

        var totalLaps = data[HeaderSize + 3];
        var trackLength = U16(data, HeaderSize + 4);
        var sessionType = data[HeaderSize + 6];
        var trackId = unchecked((sbyte)data[HeaderSize + 7]);
        var trackName = TrackNames.GetTrackName(trackId);
        var sessionName = TrackNames.GetSessionTypeName(sessionType);

        return new SessionMetadata
        {
            StartedAt = startedAt,
            TrackId = trackId,
            TrackName = trackName,
            SessionType = sessionType,
            TotalLaps = totalLaps,
            TrackLengthMeters = trackLength,
            SessionName = $"{Sanitize(trackName)}_{Sanitize(sessionName)}_{startedAt:yyyyMMdd_HHmmss}"
        };
    }

    private static int SectorMs(ushort msPart, byte minPart) => minPart * 60000 + msPart;
    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o, 2));
    private static short I16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o, 2));
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o, 4));
    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(o, 8));
    private static float F32(ReadOnlySpan<byte> s, int o) => BitConverter.ToSingle(s.Slice(o, 4));
    private static double F64(ReadOnlySpan<byte> s, int o) => BitConverter.ToDouble(s.Slice(o, 8));

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var len = 0;
        while (len < raw.Length && raw[len] != 0) len++;
        var name = Encoding.UTF8.GetString(raw.Slice(0, len)).Trim();
        if (string.IsNullOrWhiteSpace(name)) return "F1 Generic";
        if (name.Any(ch => char.IsControl(ch) || ch == '\uFFFD')) return "F1 Generic";
        return name;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Replace(' ', '_');
    }

    private static readonly Dictionary<int, string> DriverNames = new()
    {
        [0] = "Carlos Sainz", [2] = "Daniel Ricciardo", [3] = "Fernando Alonso", [7] = "Lewis Hamilton",
        [9] = "Max Verstappen", [10] = "Nico Hulkenberg", [11] = "Kevin Magnussen", [14] = "Sergio Perez",
        [15] = "Valtteri Bottas", [17] = "Esteban Ocon", [19] = "Lance Stroll", [22] = "Alexander Albon",
        [31] = "Pierre Gasly", [32] = "Yuki Tsunoda", [44] = "Lando Norris", [50] = "Charles Leclerc",
        [54] = "George Russell", [55] = "Mick Schumacher", [56] = "Oscar Piastri", [58] = "Logan Sargeant",
        [62] = "Alexander Albon", [80] = "Guanyu Zhou", [94] = "Yuki Tsunoda", [112] = "Oscar Piastri",
        [113] = "Liam Lawson", [165] = "Andrea Kimi Antonelli", [170] = "Sonny Hayes"
    };

    private static readonly Dictionary<string, string> EventNames = new()
    {
        ["SSTA"] = "Session Started", ["SEND"] = "Session Ended", ["FTLP"] = "Fastest Lap", ["RTMT"] = "Retirement",
        ["DRSE"] = "DRS Enabled", ["DRSD"] = "DRS Disabled", ["TMPT"] = "Team Mate In Pits", ["CHQF"] = "Chequered Flag",
        ["RCWN"] = "Race Winner", ["PENA"] = "Penalty Issued", ["SPTP"] = "Speed Trap", ["STLG"] = "Start Lights",
        ["LGOT"] = "Lights Out", ["DTSV"] = "Drive Through Served", ["SGSV"] = "Stop Go Served", ["FLBK"] = "Flashback",
        ["BUTN"] = "Button Status", ["RDFL"] = "Red Flag", ["OVTK"] = "Overtake", ["SCAR"] = "Safety Car", ["COLL"] = "Collision"
    };
}

public static class TrackNames
{
    private static readonly Dictionary<int, string> Tracks = new()
    {
        [0] = "Melbourne", [1] = "Paul Ricard", [2] = "China", [3] = "Bahrain", [4] = "Spain",
        [5] = "Monaco", [6] = "Montreal", [7] = "Silverstone", [8] = "Hockenheim", [9] = "Hungaroring",
        [10] = "Spa", [11] = "Monza", [12] = "Singapore", [13] = "Suzuka", [14] = "Abu Dhabi",
        [15] = "Texas", [16] = "Brazil", [17] = "Austria", [18] = "Sochi", [19] = "Mexico",
        [20] = "Baku", [21] = "Bahrain Short", [22] = "Silverstone Short", [23] = "COTA Short", [24] = "Suzuka Short",
        [25] = "Hanoi", [26] = "Zandvoort", [27] = "Imola", [28] = "Portimao", [29] = "Jeddah",
        [30] = "Miami", [31] = "Las Vegas", [32] = "Losail", [33] = "Madrid"
    };

    private static readonly Dictionary<int, string> Sessions = new()
    {
        [0] = "Unknown", [1] = "Practice 1", [2] = "Practice 2", [3] = "Practice 3", [4] = "Short Practice",
        [5] = "Qualifying 1", [6] = "Qualifying 2", [7] = "Qualifying 3", [8] = "Short Qualifying",
        [9] = "One Shot Qualifying", [10] = "Race", [11] = "Race 2", [12] = "Race 3",
        [13] = "Time Trial", [14] = "Sprint Shootout 1", [15] = "Sprint Shootout 2", [16] = "Sprint Shootout 3",
        [17] = "Sprint Race"
    };

    public static string GetTrackName(int id) => Tracks.TryGetValue(id, out var name) ? name : $"Track_{id}";
    public static string GetSessionTypeName(int id) => Sessions.TryGetValue(id, out var name) ? name : $"Session_{id}";
}
