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
    float ErsDeployedThisLap);

public sealed record CarDamageSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
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
    byte PlayerCarIndex,
    string EventCode,
    string EventName,
    int VehicleIdx,
    int OtherVehicleIdx,
    string DetailsJson);

public sealed record ParticipantPacketDebug(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    int PacketSizeBytes,
    int NumActiveCars,
    int RowsIf58Bytes,
    int RowsIf57Bytes,
    string FirstNames);

public sealed record ParticipantSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    int CarIndex,
    int AiControlled,
    int DriverId,
    int TeamId,
    int RaceNumber,
    string Name,
    int YourTelemetry,
    int ShowOnlineNames);

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
    int LapRows,
    int MotionRows,
    int StatusRows,
    int DamageRows,
    int EventsRows,
    int ParticipantsRows,
    int CleanLapCount,
    int DirtyLapCount,
    string Summary);
