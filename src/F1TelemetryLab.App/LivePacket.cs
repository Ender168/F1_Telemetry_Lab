namespace F1TelemetryLab;

// One immutable UDP envelope shared by the live consumers on the receive loop.
// Decode the frequently used arrays once; raw storage keeps the original bytes.
internal sealed class LivePacket(byte[] payload, DateTimeOffset receivedAt)
{
    public byte[] Payload { get; } = payload;
    public DateTimeOffset ReceivedAt { get; } = receivedAt;
    private List<LapDataSample>? _laps;
    private List<CarTelemetrySample>? _telemetry;
    private List<CarStatusSample>? _status;
    public List<LapDataSample> Laps => _laps ??= F12026Parser.ParseLapDataPacket(Payload, ReceivedAt);
    public List<CarTelemetrySample> Telemetry => _telemetry ??= F12026Parser.ParseCarTelemetryPacket(Payload, ReceivedAt);
    public CarTelemetrySample? PlayerTelemetry => Telemetry.FirstOrDefault(x => x.IsPlayer);
    public CarStatusSample? PlayerStatus
    {
        get
        {
            if (!F12026Parser.TryParseHeader(Payload, out var header)) return null;
            _status ??= F12026Parser.ParseCarStatusPacket(Payload, ReceivedAt, onlyCarIndex: header.PlayerCarIndex);
            return _status.FirstOrDefault(x => x.IsPlayer);
        }
    }
}
