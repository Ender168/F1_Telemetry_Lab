using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace F1TelemetryLab;

// Conservative, receipt-ordered association. Raw headers and payloads remain unchanged.
internal sealed class TerminalClassificationResolver : IDisposable
{
    private readonly SqliteCommand _terminal;
    private readonly SqliteCommand _association;
    private readonly SqliteCommand _conflict;
    private ulong _uid;
    private readonly HashSet<ulong> _retired = new();
    private DateTimeOffset? _endedAt;
    private double _endTime;
    private int _player = -1;
    private HashSet<int> _grid = new();
    private readonly Dictionary<int, LapDataSample> _laps = new();
    private readonly Dictionary<int, uint> _best = new();
    private readonly Dictionary<int, int> _gridPositions = new();
    private readonly Dictionary<int, double> _crossings = new();
    private readonly Dictionary<int, double> _finishes = new();
    private string? _fingerprint;
    private bool _conflicted;

    public TerminalClassificationResolver(SqliteConnection con, SqliteTransaction tx)
    {
        using var schema = con.CreateCommand(); schema.Transaction = tx;
        schema.CommandText = """
            DROP TABLE IF EXISTS terminal_packet_context;
            DROP TABLE IF EXISTS terminal_classification_association;
            CREATE TABLE terminal_packet_context(raw_packet_id INTEGER PRIMARY KEY, received_at TEXT,
                packet_id INTEGER, header_session_uid TEXT, session_time REAL, overall_frame_identifier INTEGER,
                candidate_session_uid TEXT, segment TEXT);
            CREATE TABLE terminal_classification_association(raw_packet_id INTEGER PRIMARY KEY,
                candidate_session_uid TEXT, associated_session_uid TEXT, status TEXT, note TEXT, fingerprint TEXT);
            """;
        schema.ExecuteNonQuery();
        _terminal = con.CreateCommand(); _terminal.Transaction = tx;
        _terminal.CommandText = "INSERT INTO terminal_packet_context VALUES($id,$at,$packet,'0',$time,$frame,$uid,'uid_zero_terminal_or_menu')";
        foreach (var p in new[] { "$id", "$at", "$packet", "$time", "$frame", "$uid" }) _terminal.Parameters.AddWithValue(p, 0);
        _association = con.CreateCommand(); _association.Transaction = tx;
        _association.CommandText = "INSERT INTO terminal_classification_association VALUES($id,$uid,$associated,$status,$note,$hash)";
        foreach (var p in new[] { "$id", "$uid", "$associated", "$status", "$note", "$hash" }) _association.Parameters.AddWithValue(p, 0);
        _conflict = con.CreateCommand(); _conflict.Transaction = tx;
        _conflict.CommandText = "UPDATE terminal_classification_association SET associated_session_uid=NULL,status='conflict',note='Conflicting UID-zero result packets; association revoked.' WHERE candidate_session_uid=$uid AND status='associated'";
        _conflict.Parameters.AddWithValue("$uid", "");
    }

    public void Observe(long id, PacketHeader h, byte[] payload, DateTimeOffset at, IReadOnlyList<LapDataSample>? parsedLaps = null)
    {
        if (h.SessionUid == 0)
        {
            _terminal.Parameters["$id"].Value = id; _terminal.Parameters["$at"].Value = at.ToString("O");
            _terminal.Parameters["$packet"].Value = h.PacketId; _terminal.Parameters["$time"].Value = h.SessionTime;
            _terminal.Parameters["$frame"].Value = h.OverallFrameIdentifier;
            _terminal.Parameters["$uid"].Value = _uid == 0 ? DBNull.Value : _uid.ToString();
            _terminal.ExecuteNonQuery();
            if (h.PacketId == 8) Associate(id, h, payload, at);
            return;
        }
        if (_retired.Contains(h.SessionUid)) return;
        if (h.SessionUid != _uid)
        {
            if (h.PacketId is not (1 or 2)) return;
            if (_uid != 0) _retired.Add(_uid);
            _uid = h.SessionUid; _player = h.PlayerCarIndex; _endedAt = null;
            _grid.Clear(); _gridPositions.Clear(); _laps.Clear(); _best.Clear(); _crossings.Clear(); _finishes.Clear();
            _fingerprint = null; _conflicted = false;
        }
        if (h.PlayerCarIndex != _player) { _grid.Clear(); _endedAt = null; return; }
        if (h.PacketId == 4)
            _grid = F12026Parser.ParseParticipantsPacket(payload, at).Select(x => x.CarIndex).ToHashSet();
        if (h.PacketId == 3 && payload.Length >= F12026Parser.HeaderSize + 4 &&
            payload.AsSpan(F12026Parser.HeaderSize, 4).SequenceEqual("SEND"u8))
        { _endedAt = at; _endTime = h.SessionTime; }
        if (h.PacketId != 2) return;
        foreach (var lap in parsedLaps ?? F12026Parser.ParseLapDataPacket(payload, at))
        {
            if (lap.ResultStatus <= 1 || lap.Position <= 0) continue;
            var gridOffset = F12026Parser.HeaderSize + lap.CarIndex * 57 + 43;
            if (gridOffset < payload.Length && payload[gridOffset] > 0)
                _gridPositions.TryAdd(lap.CarIndex, payload[gridOffset]);
            if (_laps.TryGetValue(lap.CarIndex, out var prior))
            {
                if (lap.OverallFrameIdentifier <= prior.OverallFrameIdentifier) continue;
                if (lap.LapNum == prior.LapNum + 1 && lap.LastLapTimeMs > 0)
                {
                    _crossings[lap.CarIndex] = lap.SessionTime - lap.CurrentLapTimeMs / 1000d;
                    if (!prior.LapInvalid) _best[lap.CarIndex] = Math.Min(_best.GetValueOrDefault(lap.CarIndex, uint.MaxValue), lap.LastLapTimeMs);
                }
            }
            if (lap.ResultStatus == 3) _finishes.TryAdd(lap.CarIndex, lap.SessionTime);
            _laps[lap.CarIndex] = lap;
        }
    }

    private void Associate(long id, PacketHeader h, byte[] payload, DateTimeOffset at)
    {
        var rows = F12026Parser.ParseFinalClassificationPacket(payload, at);
        var hash = Convert.ToHexString(SHA256.HashData(payload.AsSpan(F12026Parser.HeaderSize)));
        var note = Validate(h, payload, at, rows);
        // Once a plausible packet has been associated, a different result in the same
        // terminal window must revoke it, even if the second packet fails lap validation.
        var inWindow = _endedAt is { } end && at >= end && at - end <= TimeSpan.FromMinutes(3);
        if (_fingerprint is not null && hash != _fingerprint && inWindow)
        {
            _conflicted = true; _conflict.Parameters["$uid"].Value = _uid.ToString(); _conflict.ExecuteNonQuery();
        }
        if (_conflicted) note = "Conflicting UID-zero result packets; no automatic association.";
        var accepted = note is null;
        if (accepted) _fingerprint = hash;
        _association.Parameters["$id"].Value = id;
        _association.Parameters["$uid"].Value = _uid == 0 ? DBNull.Value : _uid.ToString();
        _association.Parameters["$associated"].Value = accepted ? _uid.ToString() : DBNull.Value;
        _association.Parameters["$status"].Value = accepted ? "associated" : _conflicted ? "conflict" : "unmatched";
        _association.Parameters["$note"].Value = note ?? "UID-zero packet 8 contextually associated after SEND using participant slots, completed laps, stops, player best lap and finish time. Header UID remains zero; association is inferred, not authoritative.";
        _association.Parameters["$hash"].Value = hash; _association.ExecuteNonQuery();
    }

    private string? Validate(PacketHeader h, byte[] payload, DateTimeOffset at, List<FinalClassificationSample> rows)
    {
        if (_uid == 0 || _endedAt is not { } end || at < end || at - end > TimeSpan.FromMinutes(3))
            return "No just-ended race within the terminal association window.";
        if (!float.IsFinite(h.SessionTime) || h.SessionTime < _endTime - 2 || h.SessionTime > _endTime + 180 || h.PlayerCarIndex != _player)
            return "Terminal chronology or player index differs from the ended race.";
        if (payload.Length < F12026Parser.HeaderSize + 1 || payload[F12026Parser.HeaderSize] != rows.Count ||
            _grid.Count == 0 || !_grid.SetEquals(rows.Select(x => x.CarIndex)) ||
            rows.Select(x => x.Position).Distinct().Count() != rows.Count || rows.Any(x => x.Position < 1 || x.Position > rows.Count))
            return "Participant count/grid or result packet is incomplete or inconsistent.";
        foreach (var row in rows)
        {
            if (!_laps.TryGetValue(row.CarIndex, out var lap) || row.NumLaps < 1 ||
                row.NumLaps < lap.LapNum - 1 || row.NumLaps > lap.LapNum ||
                row.NumPitStops != lap.NumPitStops || row.ResultStatus <= 2 ||
                (_gridPositions.TryGetValue(row.CarIndex, out var gridPosition) && row.GridPosition != gridPosition) ||
                !double.IsFinite(row.TotalRaceTimeSeconds) || row.TotalRaceTimeSeconds < 0)
                return "Lap/result/stop evidence differs from the ended race.";
        }
        var player = rows.SingleOrDefault(x => x.CarIndex == _player);
        if (player is null || !_finishes.ContainsKey(_player) ||
            !_best.TryGetValue(_player, out var best) || player.BestLapTimeMs != best ||
            !(Near(_crossings.GetValueOrDefault(_player, double.NaN), player.TotalRaceTimeSeconds) ||
              Near(_finishes[_player], player.TotalRaceTimeSeconds)))
            return "Insufficient matching player best-lap/finish-time evidence.";
        return null;
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 3;
    public void Dispose() { _terminal.Dispose(); _association.Dispose(); _conflict.Dispose(); }
}
