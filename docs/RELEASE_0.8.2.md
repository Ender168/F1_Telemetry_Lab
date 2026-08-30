# F1 Telemetry Lab v0.8.2

## Lean session storage

v0.8.2 reduces `session.sqlite` growth without sacrificing the raw UDP source needed for future parser fixes and re-analysis.

### Raw UDP remains authoritative

`raw_packets` is still the source of truth. The recorder keeps the packets required for race analysis, including motion, session, lap, event, participant, setup, telemetry, status, final classification, damage, session history, tyre sets, Motion Ex, lap positions and 2026 telemetry extensions.

The storage policy now avoids known low-value duplication:

- packet 9 Lobby Info is not persisted;
- packet 14 Time Trial data is not persisted for a known non-Time-Trial session;
- unchanged packet 5 Car Setup payloads are deduplicated after the first stored copy;
- packet 6 is no longer simultaneously expanded into `car_telemetry` during live recording. Live display rows stay in RAM and the derived table is rebuilt from raw packet 6 during analysis.

### Additional 2026 telemetry

Previously retained-but-unparsed 2026 packets are now exposed as queryable projections:

- packet 11 Session History -> `session_history_laps` and `session_history_stints`;
- packet 12 Tyre Sets -> `tyre_sets`, retaining the latest 20-set snapshot for each car;
- packet 16 Car Telemetry 2 -> `car_telemetry_2_player`, exposing player active-aero mode/availability/activation distance, overtake availability/activity/activation distance, 2026-regulations flag and wrong-way state.

Session History and Tyre Sets are stored as compact latest snapshots for all cars. Packet 16 remains complete for all 24 cars in `raw_packets`, while only the player-car high-frequency projection is materialized to avoid recreating the storage problem v0.8.2 is intended to solve.

### Post-analysis compaction

After a successful analysis and telemetry-completeness pass, the database adopts the storage profile `raw_plus_summaries_plus_player_detail`.

Detailed frame-level tables keep only player-car rows:

- `car_telemetry`;
- `lap_data`;
- `motion_data`;
- `car_status`;
- `car_damage`.

All-car analytical information remains available through compact products such as `lap_summary`, `lap_state_summary`, `analysis_trace_10m`, `final_classification`, `participants`, events, setup snapshots, session history and tyre-set snapshots. `motion_ex_player`, `lap_positions` and `car_telemetry_2_player` remain available because they are directly useful for post-race analysis.

Temporary/rebuildable tables are removed after analysis:

- `analysis_samples`;
- `analysis_context`;
- `final_classification_packet`.

SQLite `VACUUM` is then run so deleted rows actually reduce the physical database size.

### Rebuildability

No removed frame-level data is unique. Running the analysis pipeline again reconstructs the all-car derived tables from `raw_packets`. This preserves the v0.8.1 ability to recover newly supported fields from older recordings while keeping normal session files substantially smaller.

### Diagnostics

The manifest and `session_metadata` record the active storage profile, rows pruned, transient tables removed, database size before/after optimization and bytes saved. They also expose row counts for Session History, tyre sets and player Car Telemetry 2.
