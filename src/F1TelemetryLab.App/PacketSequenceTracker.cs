namespace F1TelemetryLab;

public readonly record struct PacketSequenceObservation(
    bool Unsupported,
    bool Duplicate,
    bool OutOfOrder,
    bool SessionChanged,
    bool SequenceIgnored);

/// <summary>
/// Applies the packet-aware sequence rules used by the live recorder. Event packets
/// share frame identifiers, packet 11 is emitted once per car, and terminal packets
/// may legally use frame zero, so none of those are transport duplicates.
/// </summary>
public sealed class PacketSequenceTracker
{
    private readonly Dictionary<(ulong SessionUid, byte PacketId), uint> _lastOverallFrames = new();
    private ulong _activeSessionUid;

    public PacketSequenceObservation Observe(PacketHeader header)
    {
        if (header.PacketFormat != AppInfo.SupportedPacketFormat)
            return new PacketSequenceObservation(true, false, false, false, false);

        var sessionChanged = false;
        if (header.SessionUid != 0 && _activeSessionUid == 0)
            _activeSessionUid = header.SessionUid;
        else if (header.SessionUid != 0 && _activeSessionUid != header.SessionUid)
        {
            _activeSessionUid = header.SessionUid;
            sessionChanged = true;
        }

        if (header.PacketId is 3 or 11 || header.OverallFrameIdentifier == 0)
            return new PacketSequenceObservation(false, false, false, sessionChanged, true);

        var key = (header.SessionUid, header.PacketId);
        var duplicate = false;
        var outOfOrder = false;
        if (_lastOverallFrames.TryGetValue(key, out var previous))
        {
            duplicate = header.OverallFrameIdentifier == previous;
            outOfOrder = header.OverallFrameIdentifier < previous;
        }

        _lastOverallFrames[key] = header.OverallFrameIdentifier;
        return new PacketSequenceObservation(false, duplicate, outOfOrder, sessionChanged, false);
    }

    public void Reset()
    {
        _activeSessionUid = 0;
        _lastOverallFrames.Clear();
    }
}
