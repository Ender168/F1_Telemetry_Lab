using System.Buffers.Binary;

namespace F1TelemetryLab;

public sealed record OpponentLap(int LapNumber, uint LapTimeMs, bool? Valid, bool PitLap, bool SafetyCarAffected);

public sealed record NearbyCarSnapshot(
    int CarIndex, int Position, bool IsPlayer, string Name,
    int? VisualCompound, int? TyreAgeLaps, bool InPit,
    DateTimeOffset PositionReceivedAt, DateTimeOffset? TyresReceivedAt,
    IReadOnlyList<OpponentLap> LastLaps);

/// <summary>Bounded live history keyed by car slot, never by changing race position.</summary>
internal sealed class NearbyCarTracker
{
    private sealed class Car
    {
        public LapDataSample? Lap;
        public bool Invalid;
        public bool Pit;
        public bool SafetyCar;
        public string Name = "";
        public int? Compound;
        public int? Age;
        public DateTimeOffset? StatusAt;
        public uint StatusFrame;
        public uint HistoryFrame;
        public List<OpponentLap> History = new(3);
    }

    private readonly Car[] _cars = Enumerable.Range(0, F12026Parser.MaxCars2026).Select(_ => new Car()).ToArray();
    private int _playerIndex = -1;
    private uint _minimumFrame;

    public void ObserveLap(LapDataSample row, bool safetyCar)
    {
        if (row.OverallFrameIdentifier < _minimumFrame) return;
        if (row.CarIndex is < 0 or >= F12026Parser.MaxCars2026) return;
        var car = _cars[row.CarIndex];
        var previous = car.Lap;
        if (previous is not null && row.OverallFrameIdentifier < previous.OverallFrameIdentifier) return;
        if (row.IsPlayer) _playerIndex = row.CarIndex;
        if (previous is not null && row.LapNum < previous.LapNum)
        {
            car.History.RemoveAll(x => x.LapNumber >= row.LapNum);
            car.Age = null;
            car.Compound = null;
            car.StatusAt = null;
        }
        if (previous is not null && row.LapNum > previous.LapNum && row.LastLapTimeMs is >= 30_000 and <= 900_000)
        {
            var adjacent = row.LapNum == previous.LapNum + 1;
            Remember(car, new OpponentLap(row.LapNum - 1, row.LastLapTimeMs,
                adjacent ? !car.Invalid : null, adjacent && car.Pit, adjacent && car.SafetyCar));
        }
        if (previous is null || row.LapNum != previous.LapNum)
        {
            car.Invalid = false;
            car.Pit = false;
            car.SafetyCar = false;
        }
        car.Invalid |= row.LapInvalid;
        car.Pit |= row.PitStatus > 0;
        car.SafetyCar |= safetyCar;
        car.Lap = row;
    }

    public void ObserveParticipants(IEnumerable<ParticipantSample> participants)
    {
        foreach (var row in participants)
            if (row.CarIndex is >= 0 and < F12026Parser.MaxCars2026)
                _cars[row.CarIndex].Name = row.Name;
    }

    public void ObserveStatus(ReadOnlySpan<byte> payload, PacketHeader header, DateTimeOffset at)
    {
        if (header.OverallFrameIdentifier < _minimumFrame) return;
        // Read only compound/age from all slots. Keep the full status parser player-only.
        const int size = 59;
        var count = Math.Min(F12026Parser.MaxCars2026, (payload.Length - F12026Parser.HeaderSize) / size);
        for (var index = 0; index < count; index++)
        {
            var car = _cars[index];
            if (car.StatusAt is not null && header.OverallFrameIdentifier < car.StatusFrame) continue;
            var row = payload.Slice(F12026Parser.HeaderSize + index * size, size);
            var compound = row[26];
            var age = unchecked((sbyte)row[27]);
            car.Compound = compound is 16 or 17 or 18 or 7 or 8 ? compound : null;
            car.Age = car.Compound is not null && age >= 0 ? age : null;
            car.StatusAt = at;
            car.StatusFrame = header.OverallFrameIdentifier;
        }
    }

    public void ObserveHistory(ReadOnlySpan<byte> payload, PacketHeader header)
    {
        if (header.OverallFrameIdentifier < _minimumFrame) return;
        const int metadata = F12026Parser.HeaderSize + 7;
        const int size = 14;
        if (payload.Length < metadata + 100 * size) return;
        var index = payload[F12026Parser.HeaderSize];
        if (index >= _cars.Length) return;
        var car = _cars[index];
        if (header.OverallFrameIdentifier < car.HistoryFrame || car.Lap is null) return;
        car.HistoryFrame = header.OverallFrameIdentifier;
        var count = Math.Min(100, (int)payload[F12026Parser.HeaderSize + 1]);
        // Packet 11 also contains the current, incomplete lap. Never show it as a finish.
        if (car.Lap.ResultStatus != 3) count = Math.Min(count, Math.Max(0, car.Lap.LapNum - 1));
        for (var i = Math.Max(0, count - 3); i < count; i++)
        {
            var row = payload.Slice(metadata + i * size, size);
            var time = BinaryPrimitives.ReadUInt32LittleEndian(row);
            if (time is < 30_000 or > 900_000) continue;
            var known = car.History.FirstOrDefault(x => x.LapNumber == i + 1);
            Remember(car, new OpponentLap(i + 1, time, (row[13] & 1) != 0,
                known?.PitLap ?? false, known?.SafetyCarAffected ?? false));
        }
    }

    public IReadOnlyList<NearbyCarSnapshot> Snapshot(DateTimeOffset now)
    {
        if (_playerIndex < 0 || _cars[_playerIndex].Lap is not { Position: > 0 } player ||
            now - player.ReceivedAt > TimeSpan.FromSeconds(5)) return Array.Empty<NearbyCarSnapshot>();
        return _cars.Select((car, index) => (car, index))
            .Where(x => x.car.Lap is { Position: > 0 } lap &&
                Math.Abs(lap.Position - player.Position) <= 2 && now - lap.ReceivedAt <= TimeSpan.FromSeconds(5))
            .OrderBy(x => x.car.Lap!.Position)
            .Select(x => new NearbyCarSnapshot(x.index, x.car.Lap!.Position, x.index == _playerIndex,
                x.car.Name, x.car.Compound, x.car.Age, x.car.Lap.PitStatus > 0,
                x.car.Lap.ReceivedAt, x.car.StatusAt, x.car.History.ToArray())).ToArray();
    }

    public void Clear()
    {
        for (var i = 0; i < _cars.Length; i++) _cars[i] = new Car();
        _playerIndex = -1;
        _minimumFrame = 0;
    }

    public void ClearAfterFlashback(uint overallFrame)
    {
        _minimumFrame = overallFrame;
        foreach (var car in _cars)
        {
            car.Lap = null;
            car.History.Clear();
            car.Age = null;
            car.Compound = null;
            car.StatusAt = null;
            car.HistoryFrame = 0;
        }
    }

    private static void Remember(Car car, OpponentLap lap)
    {
        car.History.RemoveAll(x => x.LapNumber == lap.LapNumber);
        car.History.Add(lap);
        car.History.Sort((a, b) => a.LapNumber.CompareTo(b.LapNumber));
        if (car.History.Count > 3) car.History.RemoveRange(0, car.History.Count - 3);
    }
}
