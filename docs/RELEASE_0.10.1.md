# F1 Telemetry Lab v0.10.1

## ERS tactical modes

ERS Autopilot now separates race context into three states instead of one generic battle flag:

- `NEUTRAL`: no rival is inside the tactical windows.
- `ATTACK`: the car ahead is inside `attack_gap_ms`.
- `DEFEND`: the car behind is inside `defend_gap_ms`.

If both rivals are close, defence takes priority only when the rear threat is materially closer by `defend_priority_margin_ms`. `tactical_exit_margin_ms` adds hysteresis so a gap hovering around the threshold does not make the controller oscillate between tactical states.

Every ERS decision reason is prefixed with `[NEUTRAL]`, `[ATTACK]` or `[DEFEND]`. This keeps the tactical state visible in the live status and in the existing `ers_control_events.reason` field inside `session.sqlite` without a database migration.

## JSON profile changes

New profile fields:

- `profile_revision`
- `attack_gap_ms`
- `defend_gap_ms`
- `tactical_exit_margin_ms`
- `defend_priority_margin_ms`

New rule conditions:

- `neutral`
- `attack`
- `defend`
- `attackOrHighBattery`
- `defendOrHighBattery`

Legacy `battle` and `battleOrHighBattery` conditions remain supported for old profiles.

The built-in China profile keeps `profile_id: china-race-r03-v1` for compatibility and is now `profile_revision: 2`. The unchanged stock revision 1 from v0.10.0 is upgraded automatically and backed up as `China_Race.json.pre-0.10.1.bak`. A China profile with another `profile_id` is treated as user-managed and is not overwritten.

## China baseline

The China profile now uses distinct optional Boost rules for attack and defence on T16 -> T1 and T4 -> T6 while retaining the planned T13 deployment and low-battery recovery zones.

## Safety

No changes were made to the input safety layer. Online sessions, ERS Assist, pause, pit lane, Safety Car/VSC/formation lap, stale telemetry and F12 emergency stop retain the existing hard blocks.
