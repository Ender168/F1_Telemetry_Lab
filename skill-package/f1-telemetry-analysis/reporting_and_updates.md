# Reporting and project updates

## 1. Report naming

Dry/general:
`Rxx - <Track> - <Session> - <Focus> - <Date>`

Wet:
`Wxx - <Track> - <Session> - <Focus> - <Date>`

The wet prefix is mandatory when wet development is intentionally separated from dry development.

## 2. Report structure

Recommended order:

1. Session card
2. Analysis validity / exclusions
3. Executive conclusion
4. Result and chronology
5. Clean pace
6. Theoretical lap
7. Racecraft
8. Corner performance
9. Tyres / weather
10. Fuel
11. Damage
12. Setup evaluation
13. ERS Autopilot
14. Progress versus history
15. Next-session priorities
16. Data/spec

Not every report needs equal prose in every section, but do not omit a material issue merely to keep the document short.

## 3. Analysis-validity section is mandatory

State:
- which laps/phases are excluded;
- why;
- which data remains usable for racecraft/strategy despite exclusion from pace;
- diagnostic FLBK treatment;
- confidence limitations.

This is especially important for:
- fuel saving;
- wet crossover;
- damage;
- pit-debug sessions.

## 4. Numerical corner standard

For Top-3 losses, anomalies and ERS-dependent exits include:
- best;
- median;
- weak/battle;
- absolute delta;
- relevant speed/input metrics;
- root cause;
- "How to drive it";
- measurable target.

Do not produce 18 equally verbose corner sections if only three matter.

## 5. Recommendations

End with 3-5 primary measurable priorities where possible.

A priority should specify:
- what to change;
- what remains constant;
- what success looks like.

Example:
"Front wing 43 -> 45; keep rear wing 30 and mechanics unchanged; success = lower S1 median with no T14/T18 traction regression."

## 6. Drive updates after a complete analysis

When workspace access exists, update the cumulative project:
- add the new report to `F1 Performance Reports`;
- update setup history;
- update ERS validation/spec/profile files if needed;
- update current live file in `1_Setups` only when a candidate is actually adopted;
- preserve previous reports and profile versions.

Do not claim an update succeeded unless it was verified.

## 7. Candidate vs baseline status

Every report that proposes setup/ERS changes should make the status explicit:
- current baseline;
- candidate;
- validation pending;
- adopted/rejected.

A pending candidate must not be described as the current baseline.

## 8. Application limitations

If the analysis proves the app lacks a required capability:
- document the limitation;
- state the unsafe workaround that should NOT be used;
- write a concrete feature requirement;
- create/update an issue when the workflow permits;
- preserve the spec with the track's ERS material if relevant.

## 9. Historical comparison

Use prior reports to track:
- clean median;
- theory-to-actual gap;
- racecraft loss;
- cleaned FLBK;
- damage;
- tyre limiter;
- fuel target;
- ERS validation.

Only compare identical or clearly compatible definitions.

## 10. Concision

The report should be detailed where evidence changes a decision and concise elsewhere.

Prefer:
- one strong numerical table;
- Top-3 diagnosed losses;
- a few measurable next actions.

Avoid repetitive prose restating the same conclusion in every section.
