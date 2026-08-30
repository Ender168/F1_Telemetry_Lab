namespace F1TelemetryLab;

public enum RawPacketStorageDecision
{
    Store,
    SkipLobbyInfo,
    SkipNonTimeTrialPacket,
    SkipDuplicateSetup
}

/// <summary>
/// Keeps raw UDP authoritative while avoiding packets that add no value to race analysis.
/// The policy is deliberately conservative: unknown packet formats and Time Trial packets
/// seen before session metadata is known are retained rather than discarded.
/// </summary>
public sealed class RawPacketStoragePolicy
{
    private readonly Dictionary<ulong, byte[]> _lastSetupBodyBySession = new();

    public RawPacketStorageDecision Evaluate(PacketHeader? header, byte[] payload, int knownSessionType)
    {
        if (header is not { PacketFormat: AppInfo.SupportedPacketFormat } h)
            return RawPacketStorageDecision.Store;

        if (h.PacketId == 9)
            return RawPacketStorageDecision.SkipLobbyInfo;

        // Packet 14 is Time Trial-specific. If the session type is not known yet,
        // retain it rather than risk throwing away useful data.
        if (h.PacketId == 14 && knownSessionType >= 0 && knownSessionType != 18)
            return RawPacketStorageDecision.SkipNonTimeTrialPacket;

        if (h.PacketId != 5 || payload.Length <= F12026Parser.HeaderSize)
            return RawPacketStorageDecision.Store;

        // The 29-byte packet header changes every frame. Setup contents begin after it.
        var body = payload.AsSpan(F12026Parser.HeaderSize);
        if (_lastSetupBodyBySession.TryGetValue(h.SessionUid, out var previous) && body.SequenceEqual(previous))
            return RawPacketStorageDecision.SkipDuplicateSetup;

        _lastSetupBodyBySession[h.SessionUid] = body.ToArray();
        return RawPacketStorageDecision.Store;
    }

    public void Reset() => _lastSetupBodyBySession.Clear();
}
