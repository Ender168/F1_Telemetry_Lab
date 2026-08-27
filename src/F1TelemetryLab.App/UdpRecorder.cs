using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace F1TelemetryLab;

public sealed class UdpRecorder
{
    private readonly ConcurrentDictionary<int, LiveCarRow> _liveCars = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private TelemetryDatabase? _db;
    private Task? _receiveTask;
    private SessionMetadata? _metadata;
    private long _packetsSeen;
    private long _carSamplesSeen;
    private DateTimeOffset _startedAt;

    public bool IsRecording => _cts is not null;
    public string Status { get; private set; } = "Idle";
    public SessionMetadata? CurrentSession => _metadata;
    public long PacketsSeen => Interlocked.Read(ref _packetsSeen);
    public long CarSamplesSeen => Interlocked.Read(ref _carSamplesSeen);
    public IReadOnlyList<LiveCarRow> LiveCars => _liveCars.Values.OrderBy(x => x.CarIndex).ToList();

    public event Action? Updated;
    public event Action<string>? Log;

    public void Start(int port, string rootFolder)
    {
        if (IsRecording) return;

        _startedAt = DateTimeOffset.Now;
        var sessionName = $"Unknown_Track_Unknown_Session_{_startedAt:yyyyMMdd_HHmmss}";
        var sessionFolder = Path.Combine(rootFolder, "telemetry_packs", sessionName);
        Directory.CreateDirectory(sessionFolder);
        Directory.CreateDirectory(Path.Combine(sessionFolder, "exports"));

        var dbPath = Path.Combine(sessionFolder, "session.sqlite");
        _metadata = new SessionMetadata
        {
            StartedAt = _startedAt,
            SessionName = sessionName,
            SessionFolder = sessionFolder,
            DatabasePath = dbPath
        };

        _db = new TelemetryDatabase(dbPath);
        _db.SaveMetadata(_metadata);
        _udp = new UdpClient(port) { Client = { ReceiveBufferSize = 16 * 1024 * 1024 } };
        _cts = new CancellationTokenSource();
        _packetsSeen = 0;
        _carSamplesSeen = 0;
        _liveCars.Clear();
        Status = $"Recording UDP :{port}";
        Log?.Invoke($"Started recording: {sessionFolder}");
        _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    public async Task<SessionMetadata?> StopAsync(bool createZip)
    {
        if (!IsRecording) return _metadata;

        var cts = _cts!;
        _cts = null;
        try { await cts.CancelAsync(); } catch { cts.Cancel(); }
        try { _udp?.Close(); } catch { }
        _udp?.Dispose();
        _udp = null;

        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch (Exception ex) { Log?.Invoke("Receive loop stopped with warning: " + ex.Message); }
            _receiveTask = null;
        }
        cts.Dispose();

        if (_metadata is not null)
        {
            _metadata.StoppedAt = DateTimeOffset.Now;
            try { _db?.SaveMetadata(_metadata); } catch (Exception ex) { Log?.Invoke("Metadata save warning: " + ex.Message); }
        }

        try { _db?.Dispose(); }
        catch (Exception ex) { Log?.Invoke("Database close warning: " + ex.Message); }
        _db = null;

        Status = "Stopped";

        if (_metadata is not null)
        {
            // Write manifest in the current working folder first. A friendlier folder rename is attempted later,
            // after analysis/packaging have closed their SQLite handles. Windows enjoys holding files hostage.
            try
            {
                WriteManifest(_metadata, PacketsSeen, CarSamplesSeen);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Manifest warning: " + ex.Message);
            }

            try
            {
                Log?.Invoke("Analyzing session...");
                var analysis = await AnalysisEngine.AnalyzeSessionAsync(_metadata.SessionFolder, Log);
                Log?.Invoke(analysis.Summary);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Analysis failed: " + ex.Message);
            }

            if (createZip)
            {
                try
                {
                    _metadata.ZipPath = SessionPackager.CreateZip(_metadata.SessionFolder, _metadata.DatabasePath, _metadata.SessionName);
                    Log?.Invoke("Zip created: " + _metadata.ZipPath);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Zip failed: " + ex.Message);
                }
            }

            // The physical folder intentionally stays as Unknown_*.
            // Renaming a folder that contains a just-closed SQLite/WAL database is unreliable on Windows and can create
            // duplicate multi-hundred-megabyte folders. The zip and UI use the readable session name instead.
        }

        Log?.Invoke($"Stopped. Packets: {PacketsSeen:N0}, car samples: {CarSamplesSeen:N0}");
        if (_metadata is not null)
        {
            var dbInfo = File.Exists(_metadata.DatabasePath) ? new FileInfo(_metadata.DatabasePath).Length.ToString("N0") + " bytes" : "missing";
            Log?.Invoke($"Database: {_metadata.DatabasePath} ({dbInfo})");
        }
        Updated?.Invoke();
        return _metadata;
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"UDP error: {ex.Message}");
                break;
            }

            var now = DateTimeOffset.Now;
            var data = result.Buffer;
            PacketHeader? header = null;
            if (F12026Parser.TryParseHeader(data, out var h)) header = h;
            _db?.InsertRaw(now, header, data);
            Interlocked.Increment(ref _packetsSeen);

            if (header is { PacketFormat: 2026, PacketId: 1 })
            {
                var sessionMeta = F12026Parser.TryParseSessionMetadata(data, _startedAt);
                if (sessionMeta is not null && _metadata is not null && _metadata.TrackName == "Unknown Track")
                {
                    sessionMeta.DatabasePath = _metadata.DatabasePath;
                    sessionMeta.SessionFolder = _metadata.SessionFolder;
                    _metadata.TrackName = sessionMeta.TrackName;
                    _metadata.TrackId = sessionMeta.TrackId;
                    _metadata.SessionType = sessionMeta.SessionType;
                    _metadata.TotalLaps = sessionMeta.TotalLaps;
                    _metadata.TrackLengthMeters = sessionMeta.TrackLengthMeters;
                    _metadata.SessionName = sessionMeta.SessionName;
                    _db?.SaveMetadata(_metadata);
                }
            }

            if (header is { PacketFormat: 2026, PacketId: 6 })
            {
                foreach (var sample in F12026Parser.ParseCarTelemetryPacket(data, now))
                {
                    _db?.InsertCarTelemetry(sample);
                    Interlocked.Increment(ref _carSamplesSeen);
                    var row = _liveCars.GetOrAdd(sample.CarIndex, i => new LiveCarRow { CarIndex = i });
                    row.IsPlayer = sample.IsPlayer;
                    row.Speed = sample.Speed;
                    row.Throttle = sample.Throttle;
                    row.Brake = sample.Brake;
                    row.Steer = sample.Steer;
                    row.Gear = sample.Gear;
                    row.EngineRpm = sample.EngineRpm;
                    row.Drs = sample.Drs;
                }
            }

            if (PacketsSeen % 100 == 0) Updated?.Invoke();
        }
    }

    private static void FinalizeSessionFolderName(SessionMetadata metadata)
    {
        var parent = Path.GetDirectoryName(metadata.SessionFolder);
        if (string.IsNullOrWhiteSpace(parent)) return;

        var desiredFolder = Path.Combine(parent, metadata.SessionName);
        if (string.Equals(metadata.SessionFolder, desiredFolder, StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(desiredFolder))
        {
            desiredFolder = Path.Combine(parent, metadata.SessionName + "_" + DateTimeOffset.Now.ToString("HHmmss"));
        }

        // Windows can keep a just-closed SQLite/WAL handle around for a moment.
        // Retry before giving up, because a one-shot Directory.Move is apparently too optimistic for civilization.
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Directory.Move(metadata.SessionFolder, desiredFolder);
                metadata.SessionFolder = desiredFolder;
                metadata.DatabasePath = Path.Combine(desiredFolder, "session.sqlite");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Thread.Sleep(150 * attempt);
            }
        }

        throw new IOException($"Could not rename session folder to '{desiredFolder}'. The original folder will be used. Last error: {lastError?.Message}", lastError);
    }

    private static void WriteManifest(SessionMetadata metadata, long packetsSeen, long carSamplesSeen)
    {
        var dbExists = File.Exists(metadata.DatabasePath);
        var dbSize = dbExists ? new FileInfo(metadata.DatabasePath).Length : 0L;
        var manifest = $$"""
        {
          "app": "F1 Telemetry Lab C# MVP",
          "version": "0.3.8",
          "session_name": "{{metadata.SessionName}}",
          "track_name": "{{metadata.TrackName}}",
          "track_id": {{metadata.TrackId}},
          "session_type": {{metadata.SessionType}},
          "total_laps": {{metadata.TotalLaps}},
          "track_length_m": {{metadata.TrackLengthMeters}},
          "started_at": "{{metadata.StartedAt:O}}",
          "stopped_at": "{{metadata.StoppedAt:O}}",
          "database": "session.sqlite",
          "database_exists": {{dbExists.ToString().ToLowerInvariant()}},
          "database_size_bytes": {{dbSize}},
          "packets_seen": {{packetsSeen}},
          "car_samples_seen": {{carSamplesSeen}}
        }
        """;
        File.WriteAllText(Path.Combine(metadata.SessionFolder, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(metadata.SessionFolder, "README_FOR_CHATGPT.txt"),
            "This telemetry pack was created by F1 Telemetry Lab C# MVP. Send the whole zip to ChatGPT for analysis.\n" +
            "Contains: chatgpt_pack.sqlite, manifest.json, analysis_manifest.json, and exports folder. Full session.sqlite stays local.\n");
    }
}
