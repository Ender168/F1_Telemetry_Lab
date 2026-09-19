# Corner and segment analysis

The corner section is diagnostic, not decorative.

## 1. Fixed segment boundaries

Each track should use stable segment boundaries for repeated analysis.

Once a boundary set is accepted, reuse it unless there is a documented reason to revise it.

Inter-session time deltas are allowed only when boundaries are identical.

If old and new boundaries differ:
- use within-session best/median/weak;
- do not claim a precise inter-session delta.

## 2. Analyse all, explain the important ones

Calculate broad corner/segment metrics where possible, but provide full prose diagnosis only for:
- Top-3 material losses;
- ERS-dependent exits;
- anomalous/unsafe corners;
- user-identified problem areas;
- any corner central to the current setup A/B.

This prevents the report from burying the important findings under 18 equally long corner paragraphs.

## 3. Mandatory numerical block for a problematic segment

Include, where data supports it:
- segment boundaries;
- sample definition;
- n;
- best;
- representative median;
- weak or battle pass;
- absolute loss versus best/reference;
- entry speed;
- minimum speed;
- exit speed;
- brake pattern;
- throttle pattern;
- steering pattern;
- yaw/slip;
- ERS onset/state;
- traffic context.

Do not include a metric merely because it exists. Use it when it explains the delta.

## 4. Root-cause tagging

Assign one or more:
- Entry
- Rotation
- Traction
- ERS
- Traffic
- Tyre
- Fuel
- Damage

Examples:
- low minimum speed after too much brake: Entry/Rotation;
- repeated second steering input before throttle: Rotation;
- rear slip after Boost onset: Traction/ERS;
- normal inputs but slower exit in dirty air: Traffic.

## 5. Good vs weak comparison

A good comparison should explain the causal sequence, not only the final time.

Example structure:
- Good pass: brake release at X, min Y, steering unwinds before throttle, exit Z.
- Weak pass: extra brake/second correction, min lower by N km/h, throttle delayed by M ms, segment +0.28 s.

## 6. "How to drive it" is mandatory

For each fully diagnosed problem corner include an explicit action sequence.

Good:
- finish the main rotation once;
- begin unwinding immediately after apex;
- add throttle as steering angle falls;
- do not add a second major steering input.

Bad:
- "be smoother";
- "carry more speed";
- "improve the exit".

## 7. Targets

Where evidence is sufficient, set a practical target:
- segment time range;
- minimum speed range;
- exit speed;
- steering correction criterion.

Targets must be based on repeatable strong passes, not one outlier.

## 8. ERS-dependent exits

For exits affected by deployment, add the ERS traction analysis from `ers_autopilot.md`.

A corner is not fully diagnosed if the mechanical and power-delivery effects are treated as unrelated.
