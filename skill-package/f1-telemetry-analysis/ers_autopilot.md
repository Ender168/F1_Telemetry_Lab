# ERS Autopilot analysis and validation

ERS must be analysed as both an energy-management system and a traction-sensitive control system.

## 1. Record actual runtime identity

Every ERS-enabled report should identify:
- F1TelemetryLab version;
- profile filename;
- profile_id;
- profile_revision;
- selection_priority;
- Dry Run vs Live;
- whether the expected profile was actually selected;
- relevant known engine limitation.

Do not assume the intended JSON was the one used.

## 2. Profile selection safety

Check:
- track_id;
- session_type;
- track length;
- weather restriction;
- priority interactions;
- actual tyre-compound support when relevant.

If the application cannot distinguish a state needed for safe profile selection, do not create multiple "live" profiles that can conflict.

Create:
- a strategy/specification;
- an application feature requirement;
- a pending validation plan.

## 3. Energy plan

Analyse:
- SOC at checkpoints;
- target vs actual;
- minimum corridor;
- recovery zones;
- surplus release;
- superclipping;
- battle spend;
- pit-lap burn;
- closing-lap drawdown;
- final-lap burn;
- finish SOC.

A good energy profile must not merely avoid empty battery. It should spend energy in high-value zones and avoid carrying excessive SOC to the finish.

## 4. Final SOC

Report:
- finish SOC;
- target range;
- whether excess energy remained;
- whether energy was wasted through clipping earlier.

For a profile with final-lap release, a high finish SOC requires explanation.

Do not fix high final SOC by moving Boost earlier into an unsafe traction phase merely because energy remains.

## 5. Traction-safe onset

For every important increase to Hotlap/Boost after a slow/medium corner, inspect the car state at onset.

Where available record:
- distance/time;
- speed;
- throttle;
- front wheel angle;
- yaw rate;
- rear slip angle;
- rear slip ratio;
- stable duration;
- current and target ERS mode;
- traction-wait reason.

Compare good / median / weak exits.

A distance-only rule is not sufficient evidence of safety.

## 6. Post-onset safety

Onset safety and active-deployment safety are different.

After Hotlap/Boost engages, inspect whether:
- yaw rises sharply;
- rear slip rises;
- countersteer appears;
- MGU-K is cut only after instability starts.

If the engine only gates onset and does not downgrade an already active mode, state that limitation explicitly.

Possible future active safety should use hysteresis and avoid F7/F8 thrashing.

## 7. Traction gate validation

For configured gates validate:
- required signals are fresh;
- stable_for_ms is actually satisfied;
- duplicates do not advance stability;
- unsafe sample resets stability;
- long sample gap resets stability;
- pause/lap/session/FLBK reset state;
- waiting does not consume an unstarted once-per-lap rule or its timer.

Report waiting reasons where available.

## 8. Tactical deployment

Assess:
- attack/defend thresholds;
- gap ahead/behind;
- closing rate;
- whether tactical deployment uses the highest-value straight;
- whether defending causes energy starvation later.

Do not judge tactical decisions without race context.

## 9. Pit-lap mode

Validate:
- manual trigger;
- cancellation if pit aborted;
- burn zones;
- target SOC;
- pit-lane blocking;
- flashback handling.

Pit/debug flashbacks during testing must not be treated as driving errors.

## 10. Flashback reset

For app validation, compare confirmed FLBK events against ERS reset/audit behaviour.

Desired:
- reset state 1:1 with confirmed FLBK;
- no stale active rule;
- no stale stability timer;
- fresh Session/Lap/Telemetry/Status before Live resumes.

## 11. Wet compound strategy

Intermediate and Full Wet must be treated as separate ERS regimes.

Preferred architecture:
- actual player tyre compound enters runtime;
- profile/rule selection can filter by compound;
- compound change after pit triggers safe profile re-selection;
- active rule/timing state resets safely;
- audit records old/new compound and old/new profile.

Until compound-aware selection exists:
- do not let separate Inter/Wet live profiles conflict by priority;
- use a specification/manual analysis rather than pretending the selector supports them.

## 12. Candidate lifecycle

ERS candidate workflow:
1. identify telemetry problem;
2. propose rule/profile change;
3. save candidate/spec;
4. define validation metrics;
5. Dry Run if appropriate;
6. Live validation;
7. PASS/PARTIAL/FAIL;
8. update current live track file only after adoption.

Keep previous versions.

## 13. Mandatory ERS KPIs in reports

For ERS-dependent exits:
- onset distance/time;
- speed/throttle;
- steering/yaw/rear slip;
- good vs weak segment time;
- countersteer after onset;
- SOC before/after;
- next high-value energy zone.

For profile-level validation:
- checkpoint SOC;
- clipping;
- finish SOC;
- waiting/block reasons;
- FLBK reset;
- pit-lap behaviour;
- profile identity.
