# F1 Telemetry Lab v0.8.1

## Telemetry completeness and raw recovery

v0.8.1 extends the analysis-first workflow introduced in v0.8.0. The raw UDP database remains the authoritative source, and newly supported analysis fields can be reconstructed from existing `session.sqlite` recordings when the required raw packets are present.

### Car Telemetry packet 6

The analysis database now recovers the fields that were previously ignored after the basic driving controls:

- brake temperature for RL/RR/FL/FR;
- tyre surface temperature for RL/RR/FL/FR;
- tyre inner temperature for RL/RR/FL/FR;
- actual tyre pressure for RL/RR/FL/FR;
- tyre surface type for RL/RR/FL/FR;
- engine temperature;
- clutch and rev-light fields.

These fields are added to `car_telemetry`, which makes tyre-wear anomalies much easier to separate into thermal, pressure and driving causes.

### Motion Ex packet 13

A new `motion_ex_player` table is rebuilt from raw packet 13 data. It contains the extended player-car chassis and wheel telemetry, including:

- suspension position, velocity and acceleration;
- wheel speed;
- wheel slip ratio and slip angle;
- lateral, longitudinal and vertical wheel forces;
- local and angular velocity/acceleration;
- aero heights, roll, chassis yaw/pitch;
- wheel camber and camber gain.

### Lap Positions packet 15

A new `lap_positions` table stores the position history delivered by packet 15. When packet 8 is absent, packet 15 can backfill a missing final position while the classification remains explicitly provisional.

### Final classification capture

When a race finish event has been observed and packet 8 has not yet arrived, stopping the recorder now keeps the UDP socket alive for up to eight seconds. If packet 8 arrives during that window the result is treated as official. Otherwise the existing Lap Data reconstruction remains available and is marked provisional.

### 2026 Season Pack session types

The 2026 session-type semantics are handled separately from the older mapping. Analysis metadata now preserves:

- `raw_session_type`;
- `raw_session_name`;
- `inferred_session_kind`;
- `session_type_conflict` and a conflict note.

Race evidence such as lights-out / chequered-flag or race-winner events can therefore identify a race even if the DLC reports a Sprint Shootout session type. The original UDP value is never overwritten.

### Existing recordings

`TelemetryCompletenessService` is idempotent and runs from the normal analysis/manifest refresh path. Re-analyzing an older `session.sqlite` can therefore recover the newly supported telemetry without rerecording the race, provided the corresponding raw UDP packets were saved.

### Analysis-pack behaviour

The full consistent `session.sqlite` snapshot remains included in the analysis ZIP and is authoritative. `chatgpt_pack.sqlite` continues to be a smaller convenience projection.
