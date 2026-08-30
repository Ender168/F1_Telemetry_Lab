namespace F1TelemetryLab;

public sealed record PacketHeader(
    ushort PacketFormat,
    byte GameYear,
    byte GameMajorVersion,
    byte GameMinorVersion,
    byte PacketVersion,
    byte PacketId,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    byte SecondaryPlayerCarIndex);

public sealed record CarTelemetrySample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    int CarIndex,
    bool IsPlayer,
    int Speed,
    float Throttle,
    float Brake,
    float Steer,
    int Gear,
    int EngineRpm,
    int Drs);

public sealed record CarSetupSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    int CarIndex,
    bool IsPlayer,
    int FrontWing,
    int RearWing,
    int OnThrottle,
    int OffThrottle,
    float FrontCamber,
    float RearCamber,
    float FrontToe,
    float RearToe,
    int FrontSuspension,
    int RearSuspension,
    int FrontAntiRollBar,
    int RearAntiRollBar,
    int FrontRideHeight,
    int RearRideHeight,
    int BrakePressure,
    int BrakeBias,
    int EngineBraking,
    float RearLeftTyrePressure,
    float RearRightTyrePressure,
    float FrontLeftTyrePressure,
    float FrontRightTyrePressure,
    int Ballast,
    float FuelLoad,
    float? NextFrontWingValue);

public sealed record LapDataSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    int CarIndex,
    bool IsPlayer,
    uint LastLapTimeMs,
    uint CurrentLapTimeMs,
    int Sector1TimeMs,
    int Sector2TimeMs,
    int DeltaToCarInFrontMs,
    int DeltaToRaceLeaderMs,
    float LapDistance,
    float TotalDistance,
    int Position,
    int LapNum,
    int PitStatus,
    int NumPitStops,
    int Sector,
    bool LapInvalid,
    int Penalties,
    int Warnings,
    int DriverStatus,
    int ResultStatus);

public sealed record MotionSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    int CarIndex,
    bool IsPlayer,
    float WorldPositionX,
    float WorldPositionY,
    float WorldPositionZ,
    float WorldVelocityX,
    float WorldVelocityY,
    float WorldVelocityZ,
    float GForceLateral,
    float GForceLongitudinal,
    float GForceVertical,
    float Yaw,
    float Pitch,
    float Roll);

public sealed record CarStatusSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    int CarIndex,
    bool IsPlayer,
    int FrontBrakeBias,
    float FuelInTank,
    float FuelRemainingLaps,
    int ActualTyreCompound,
    int VisualTyreCompound,
    int TyresAgeLaps,
    float EnginePowerIce,
    float EnginePowerMguk,
    float ErsStoreEnergy,
    int ErsDeployMode,
    float ErsHarvestedThisLapMguk,
    float ErsHarvestedThisLapMguh,
    float ErsHarvestLimitPerLap,
    float ErsDeployedThisLap,
    bool NetworkPaused);

public sealed record CarDamageSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    int CarIndex,
    bool IsPlayer,
    float TyreWearRl,
    float TyreWearRr,
    float TyreWearFl,
    float TyreWearFr,
    float TyreWearAvg,
    int TyreDamageRl,
    int TyreDamageRr,
    int TyreDamageFl,
    int TyreDamageFr,
    int FrontLeftWingDamage,
    int FrontRightWingDamage,
    int RearWingDamage,
    int FloorDamage,
    int DiffuserDamage,
    int SidepodDamage);

public sealed record EventSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    string EventCode,
    string EventName,
    int VehicleIdx,
    int OtherVehicleIdx,
    string DetailsJson);

public sealed record FlashbackSignal(
    ulong SessionUid,
    DateTimeOffset ReceivedAt,
    float EventSessionTime,
    uint OverallFrameIdentifier,
    uint TargetFrameIdentifier,
    float TargetSessionTime);

public sealed record ParticipantPacketDebug(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    int PacketSizeBytes,
    int NumActiveCars,
    int RowsIf60Bytes,
    int RowsIf58Bytes,
    string FirstNames);

public sealed record ParticipantSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    int CarIndex,
    int AiControlled,
    int DriverId,
    int TeamId,
    int RaceNumber,
    string Name,
    int YourTelemetry,
    int ShowOnlineNames);

public sealed record FinalClassificationSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    int CarIndex,
    bool IsPlayer,
    int Position,
    int NumLaps,
    int GridPosition,
    int Points,
    int NumPitStops,
    int ResultStatus,
    uint BestLapTimeMs,
    double TotalRaceTimeSeconds,
    int PenaltiesTimeSeconds,
    int NumPenalties,
    int NumTyreStints,
    int ResultReason);

public enum LapState
{
    Complete,
    PartialStart,
    PartialEnd,
    Invalid,
    Rewound
}

public sealed record LapQualityResult(
    ulong SessionUid,
    int CarIndex,
    int LapNum,
    bool IsPlayer,
    LapState State,
    bool CleanLap,
    int RewindCount,
    int InvalidCount,
    int SampleCount,
    float MinDistance,
    float MaxDistance,
    uint LapTimeMs,
    int Sector1TimeMs,
    int Sector2TimeMs,
    int Sector3TimeMs,
    uint ActiveFromOverallFrame,
    string CompletionEvidence);

public sealed record RewindEventResult(
    ulong SessionUid,
    int CarIndex,
    int LapNum,
    DateTimeOffset ReceivedAt,
    float SessionTime,
    uint OverallFrameIdentifier,
    float LapDistance,
    uint CurrentLapTimeMs,
    string Reason);

public sealed record SuspectedStateResetResult(
    ulong SessionUid,
    int CarIndex,
    int LapNum,
    DateTimeOffset ReceivedAt,
    float SessionTime,
    uint OverallFrameIdentifier,
    float LapDistance,
    uint CurrentLapTimeMs,
    string Reason);

public sealed record RecordingQualitySnapshot(
    long PacketsReceived,
    long CarSamplesWritten,
    long InvalidHeaders,
    long UnsupportedPackets,
    long DuplicateFrames,
    long OutOfOrderFrames,
    long EstimatedMissingFrames,
    long QueueDrops,
    int QueueDepth,
    int QueueHighWatermark,
    int SessionChanges,
    bool MissingFrameEstimateAvailable = false)
{
    public string CaptureRating
    {
        get
        {
            var invalidLimit = Math.Max(3, PacketsReceived / 1_000);
            var missingLimit = Math.Max(100, PacketsReceived / 100);
            if (QueueDrops > 0 || InvalidHeaders > invalidLimit ||
                (MissingFrameEstimateAvailable && EstimatedMissingFrames > missingLimit))
                return "Unreliable";
            if (InvalidHeaders > 0 || UnsupportedPackets > 0 ||
                (MissingFrameEstimateAvailable && EstimatedMissingFrames > 0) ||
                DuplicateFrames > 0 || OutOfOrderFrames > 0 || SessionChanges > 0)
                return "Usable with warnings";
            return "Good";
        }
    }

    public string Rating
    {
        get => CaptureRating;
    }
}

public sealed class LiveCarRow
{
    public int CarIndex { get; init; }
    public bool IsPlayer { get; set; }
    public int Speed { get; set; }
    public float Throttle { get; set; }
    public float Brake { get; set; }
    public float Steer { get; set; }
    public int Gear { get; set; }
    public int EngineRpm { get; set; }
    public int Drs { get; set; }

    public string Display =>
        $"{(IsPlayer ? "YOU " : "    ")}#{CarIndex:00}  {Speed,3} km/h  T:{Throttle,4:0.00}  B:{Brake,4:0.00}  S:{Steer,5:0.00}  G:{Gear,2}  RPM:{EngineRpm,5}  DRS:{Drs}";
}

public sealed class SessionMetadata
{
    public string SessionName { get; set; } = "Unknown_Track_Unknown_Session";
    public string TrackName { get; set; } = "Unknown Track";
    public int TrackId { get; set; } = -1;
    public int SessionType { get; set; } = -1;
    public int TotalLaps { get; set; } = 0;
    public int TrackLengthMeters { get; set; } = 0;
    public ulong SessionUid { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StoppedAt { get; set; }
    public string DatabasePath { get; set; } = "";
    public string SessionFolder { get; set; } = "";
    public string ZipPath { get; set; } = "";
}

public sealed record AnalysisResult(
    string SessionFolder,
    string ExportsFolder,
    int RawPacketsProcessed,
    int TelemetryRows,
    int LapRows,
    int MotionRows,
    int StatusRows,
    int DamageRows,
    int SetupRows,
    int EventsRows,
    int ParticipantsRows,
    int FinalClassificationRows,
    int ConfirmedRewindRows,
    int SuspectedStateResetRows,
    int CleanLapCount,
    int DirtyLapCount,
    string Summary);
