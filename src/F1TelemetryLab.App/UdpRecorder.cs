using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace F1TelemetryLab;

public sealed class UdpRecorder : IAsyncDisposable
{
    private const int PacketQueueCapacity = 8192;

    private sealed record QueuedPacket(DateTimeOffset ReceivedAt, PacketHeader? Header, byte[] Payload);

    private readonly ConcurrentDictionary<int, LiveCarRow> _liveCars = new();
    private readonly ConcurrentDictionary<(ulong SessionUid, byte PacketId), uint> _lastOverallFrames = new();
    private readonly object _lifecycleSync = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private TelemetryDatabase? _db;
    private Channel<QueuedPacket>? _packetQueue;
    private Task? _receiveTask;
    private Task? _writerTask;
    private Task<SessionMetadata?>? _stopTask;
    private SessionMetadata? _metadata;
    private DateTimeOffset _startedAt;
    private Exception? _backgroundError;
    private ulong _activeSessionUid;
    private long _packetsSeen;
    private long _carSamplesSeen;
    private long _invalidHeaders;
    private long _unsupportedPackets;
    private long _duplicateFrames;
    private long _outOfOrderFrames;
    private long _queueDrops;
    private int _queueDepth;
    private int _queueHighWatermark;
    private int _sessionChanges;

    public bool IsRecording => _cts is not null;
    public bool IsStopping
    {
        get
        {
            lock (_lifecycleSync) return _stopTask is { IsCompleted: false };
        }
    }
    public bool IsActive => IsRecording || IsStopping;
    public string Status { get; private set; } = "Idle";
    public SessionMetadata? CurrentSession => _metadata;
    public long PacketsSeen => Interlocked.Read(ref _packetsSeen);
    public long CarSamplesSeen => Interlocked.Read(ref _carSamplesSeen);
    public IReadOnlyList<LiveCarRow> LiveCars => _liveCars.Values.OrderBy(x => x.CarIndex).ToList();
    public RecordingQualitySnapshot Quality => new(
        PacketsSeen,
        CarSamplesSeen,
        Interlocked.Read(ref _invalidHeaders),
        Interlocked.Read(ref _unsupportedPackets),
        Interlocked.Read(ref _duplicateFrames),
        Interlocked.Read(ref _outOfOrderFrames),
        0,
        Interlocked.Read(ref _queueDrops),
        Volatile.Read(ref _queueDepth),
        Volatile.Read(ref _queueHighWatermark),
        Volatile.Read(ref _sessionChanges));

    public event Action? Updated;
    public event Action<string>? Log;

    public void Start(int port, string rootFolder)
    {
        if (IsActive) return;

        UdpClient? udp = null;
        TelemetryDatabase? database = null;
        try
        {
            udp = new UdpClient(port);
            udp.Client.ReceiveBufferSize = 16 * 1024 * 1024;

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

            database = new TelemetryDatabase(dbPath);
            database.SaveMetadata(_metadata);

            _packetQueue = Channel.CreateBounded<QueuedPacket>(new BoundedChannelOptions(PacketQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _cts = new CancellationTokenSource();
            _stopTask = null;
            _udp = udp;
            _db = database;
            udp = null;
            database = null;

            ResetCounters();
            Status = $"Recording UDP :{port}";
            Log?.Invoke($"Started recording: {sessionFolder}");
            _writerTask = Task.Run(WriterLoop);
            _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch
        {
            database?.Dispose();
            udp?.Dispose();
            _cts?.Dispose();
            _cts = null;
            _udp = null;
            _db = null;
            _packetQueue = null;
            Status = "Start failed";
            throw;
        }
    }

    public Task<SessionMetadata?> StopAsync(bool createZip)
    {
        lock (_lifecycleSync)
        {
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_cts is null) return Task.FromResult(_metadata);
            _stopTask = StopCoreAsync(createZip);
            return _stopTask;
        }
    }

    private async Task<SessionMetadata?> StopCoreAsync(bool createZip)
    {
        var cts = _cts;
        if (cts is null) return _metadata;
        _cts = null;
        Status = "Stopping";
        Updated?.Invoke();

        try { await cts.CancelAsync(); } catch { cts.Cancel(); }
        try { _udp?.Close(); } catch { }
        _udp?.Dispose();
        _udp = null;

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch (Exception ex) { RegisterBackgroundError("Receive loop", ex); }
            _receiveTask = null;
        }

        _packetQueue?.Writer.TryComplete();
        if (_writerTask is not null)
        {
            try { await _writerTask; }
            catch (Exception ex) { RegisterBackgroundError("Database writer", ex); }
            _writerTask = null;
        }
        cts.Dispose();

        if (_metadata is not null)
        {
            _metadata.StoppedAt = DateTimeOffset.Now;
            try
            {
                _db?.SaveQuality(Quality);
                _db?.SaveMetadata(_metadata);
            }
            catch (Exception ex)
            {
                RegisterBackgroundError("Final metadata save", ex);
            }
        }

        try { _db?.Dispose(); }
        catch (Exception ex) { RegisterBackgroundError("Database close", ex); }
        _db = null;
        _packetQueue = null;

        Status = _backgroundError is null ? "Stopped" : "Stopped with errors";
        if (_metadata is not null)
        {
            try { WriteManifest(_metadata, Quality, _backgroundError); }
            catch (Exception ex) { Log?.Invoke("Manifest warning: " + ex.Message); }

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
        }

        Log?.Invoke($"Stopped. Packets: {PacketsSeen:N0}, car samples: {CarSamplesSeen:N0}, quality: {Quality.Rating}");
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
        try
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

                var now = DateTimeOffset.Now;
                PacketHeader? header = null;
                if (F12026Parser.TryParseHeader(result.Buffer, out var parsed))
                {
                    header = parsed;
                    TrackHeader(parsed);
                }
                else
                {
                    Interlocked.Increment(ref _invalidHeaders);
                }

                Interlocked.Increment(ref _packetsSeen);
                var queued = new QueuedPacket(now, header, result.Buffer);
                var depth = Interlocked.Increment(ref _queueDepth);
                if (_packetQueue is null || !_packetQueue.Writer.TryWrite(queued))
                {
                    Interlocked.Decrement(ref _queueDepth);
                    Interlocked.Increment(ref _queueDrops);
                    continue;
                }

                UpdateHighWatermark(depth);
                if (PacketsSeen % 100 == 0) Updated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            RegisterBackgroundError("UDP receive", ex);
            Status = "UDP receive error";
            Updated?.Invoke();
        }
        finally
        {
            _packetQueue?.Writer.TryComplete();
        }
    }

    private async Task WriterLoop()
    {
        var queue = _packetQueue ?? throw new InvalidOperationException("Packet queue is not initialized.");
        try
        {
            await foreach (var packet in queue.Reader.ReadAllAsync())
            {
                Interlocked.Decrement(ref _queueDepth);
                _db?.InsertRaw(packet.ReceivedAt, packet.Header, packet.Payload);

                if (packet.Header is { PacketFormat: AppInfo.SupportedPacketFormat, PacketId: 1 })
                    UpdateSessionMetadata(packet);

                if (packet.Header is { PacketFormat: AppInfo.SupportedPacketFormat, PacketId: 6 })
                {
                    foreach (var sample in F12026Parser.ParseCarTelemetryPacket(packet.Payload, packet.ReceivedAt))
                    {
                        _db?.InsertCarTelemetry(sample);
                        Interlocked.Increment(ref _carSamplesSeen);
                        UpdateLiveCar(sample);
                    }
                }
            }
            _db?.Flush();
        }
        catch (Exception ex)
        {
            RegisterBackgroundError("Database writer", ex);
            Status = "Database writer error";
            try { _udp?.Close(); } catch { }
            Updated?.Invoke();
            throw;
        }
    }

    private void UpdateSessionMetadata(QueuedPacket packet)
    {
        var sessionMeta = F12026Parser.TryParseSessionMetadata(packet.Payload, _startedAt);
        if (sessionMeta is null || _metadata is null || packet.Header is null) return;

        var firstMetadata = _metadata.TrackName == "Unknown Track";
        var sessionChanged = _metadata.SessionUid != 0 && _metadata.SessionUid != packet.Header.SessionUid;
        if (!firstMetadata && !sessionChanged) return;

        _metadata.SessionUid = packet.Header.SessionUid;
        _metadata.TrackName = sessionMeta.TrackName;
        _metadata.TrackId = sessionMeta.TrackId;
        _metadata.SessionType = sessionMeta.SessionType;
        _metadata.TotalLaps = sessionMeta.TotalLaps;
        _metadata.TrackLengthMeters = sessionMeta.TrackLengthMeters;
        _metadata.SessionName = sessionChanged
            ? $"{sessionMeta.SessionName}_segment_{_sessionChanges + 1}"
            : sessionMeta.SessionName;
        _db?.SaveMetadata(_metadata);
        if (sessionChanged) Log?.Invoke($"New session UID detected: {packet.Header.SessionUid}. Data is kept in a separate logical segment.");
    }

    private void TrackHeader(PacketHeader header)
    {
        if (header.PacketFormat != AppInfo.SupportedPacketFormat)
        {
            Interlocked.Increment(ref _unsupportedPackets);
            return;
        }

        if (header.SessionUid != 0 && _activeSessionUid == 0)
            _activeSessionUid = header.SessionUid;
        else if (header.SessionUid != 0 && _activeSessionUid != header.SessionUid)
        {
            _activeSessionUid = header.SessionUid;
            Interlocked.Increment(ref _sessionChanges);
        }

        var key = (header.SessionUid, header.PacketId);
        if (_lastOverallFrames.TryGetValue(key, out var previous))
        {
            if (header.OverallFrameIdentifier == previous)
                Interlocked.Increment(ref _duplicateFrames);
            else if (header.OverallFrameIdentifier < previous)
                Interlocked.Increment(ref _outOfOrderFrames);
        }
        _lastOverallFrames[key] = header.OverallFrameIdentifier;
    }

    private void UpdateLiveCar(CarTelemetrySample sample)
    {
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

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _packetsSeen, 0);
        Interlocked.Exchange(ref _carSamplesSeen, 0);
        Interlocked.Exchange(ref _invalidHeaders, 0);
        Interlocked.Exchange(ref _unsupportedPackets, 0);
        Interlocked.Exchange(ref _duplicateFrames, 0);
        Interlocked.Exchange(ref _outOfOrderFrames, 0);
        Interlocked.Exchange(ref _queueDrops, 0);
        Interlocked.Exchange(ref _queueDepth, 0);
        Interlocked.Exchange(ref _queueHighWatermark, 0);
        Interlocked.Exchange(ref _sessionChanges, 0);
        _activeSessionUid = 0;
        _backgroundError = null;
        _lastOverallFrames.Clear();
        _liveCars.Clear();
    }

    private void UpdateHighWatermark(int depth)
    {
        var current = Volatile.Read(ref _queueHighWatermark);
        while (depth > current)
        {
            var observed = Interlocked.CompareExchange(ref _queueHighWatermark, depth, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private void RegisterBackgroundError(string source, Exception exception)
    {
        _backgroundError ??= exception;
        Log?.Invoke($"{source}: {exception.Message}");
    }

    private static void WriteManifest(SessionMetadata metadata, RecordingQualitySnapshot quality, Exception? backgroundError)
    {
        var dbExists = File.Exists(metadata.DatabasePath);
        var dbSize = dbExists ? new FileInfo(metadata.DatabasePath).Length : 0L;
        var manifest = new
        {
            app = AppInfo.Name,
            version = AppInfo.Version,
            schema_version = AppInfo.DatabaseSchemaVersion,
            session_name = metadata.SessionName,
            session_uid = metadata.SessionUid.ToString(),
            track_name = metadata.TrackName,
            track_id = metadata.TrackId,
            session_type = metadata.SessionType,
            total_laps = metadata.TotalLaps,
            track_length_m = metadata.TrackLengthMeters,
            started_at = metadata.StartedAt.ToString("O"),
            stopped_at = metadata.StoppedAt?.ToString("O"),
            database = "session.sqlite",
            database_exists = dbExists,
            database_size_bytes = dbSize,
            recording_quality = quality,
            background_error = backgroundError?.Message
        };
        File.WriteAllText(
            Path.Combine(metadata.SessionFolder, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(metadata.SessionFolder, "README_FOR_CHATGPT.txt"),
            "This telemetry pack was created by F1 Telemetry Lab. Send the whole zip to ChatGPT for analysis.\n" +
            "The manifest includes recording-quality diagnostics. Full session.sqlite remains local.\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (IsActive) await StopAsync(createZip: false);
    }
}
