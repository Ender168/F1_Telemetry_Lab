# F1 Telemetry Lab v0.10.2

## Advanced ERS profile schema

The ERS Autopilot now supports `schema_version: 2` while retaining compatibility with schema 1 profiles.

- Cyclic SOC targets and minimum corridors are interpolated between track checkpoints.
- Every deployment rule has a `deployment_value`; lower-value zones require a larger reserve above the local minimum.
- Gap trends are calculated over a configurable time window.
- ATTACK and DEFEND now have PRESSURE and CRITICAL intensities.
- Rapid closing can enter PRESSURE slightly outside the static gap threshold.
- Rules can require DRS active or inactive.
- Closing-lap and final-lap conditions use the actual race lap count.
- Final-lap release can use a lower rule-specific floor without bypassing the global critical reserve.
- Decision reasons record SOC target/minimum, next-checkpoint projection, DRS state, gap trends and laps remaining in `session.sqlite`.

## China reference profile

`China_Race.json` is upgraded to `china-race-advanced-v2`, revision 3. It defines nine SOC checkpoints and values the deployment zones as follows:

1. T13 -> T14: primary deployment zone.
2. T16 -> T1: secondary tactical zone.
3. T4 -> T6: lower-value zone, used only with tactical need or genuine surplus.

The main straight differentiates DRS and non-DRS attack behavior. The final three laps receive a controlled release, and the final lap can deploy down to the configured release floor while the 12% critical reserve remains protected.

## Upgrade behavior

The stock China profile from v0.10.0 or v0.10.1 is backed up as `China_Race.json.pre-0.10.2.bak` and replaced once. User profiles with another `profile_id` are preserved.
