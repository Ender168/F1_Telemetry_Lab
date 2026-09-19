---
name: f1-telemetry-analysis
version: 2.0.0
description: Evidence-first analysis of F1 25 telemetry sessions, including data validity, race pace, corner performance, racecraft, tyres, fuel, setup A/B testing, ERS Autopilot validation, wet conditions, and cumulative project updates.
---

# F1 Telemetry Analysis

Use this skill for F1 25 telemetry analysis when the user provides a session archive, SQLite database, exported telemetry, setup history, ERS profile, or asks for a race/session review.

The purpose is not merely to describe what happened. The analysis must determine:
1. which data is valid for each question;
2. where time or performance was actually lost;
3. what caused the loss;
4. how large the effect was;
5. what should be changed or tested next;
6. whether the proposed change is isolated enough to learn from.

## Core principle: validity before performance

Never calculate a performance conclusion before determining whether the compared samples are compatible.

User-provided session context is a hard analysis constraint. Examples:
- "from lap 19 I was critically fuel-saving";
- "these flashbacks were for pit-stop bug testing";
- "I changed only front suspension";
- "this run used Full Wet, then Intermediate";
- "this ERS profile was Dry Run, not Live".

Verify the context against telemetry where possible, but do not silently discard it.

## Mandatory workflow

Read and follow the supporting files in this order:

1. `workspace.md`
2. `data_quality_and_segmentation.md`
3. `session_analysis.md`
4. `racecraft.md`
5. `corner_analysis.md`
6. `tyres_and_conditions.md`
7. `setup_testing.md`
8. `ers_autopilot.md`
9. `reporting_and_updates.md`

`setup_and_ers.md` is retained only as a compatibility pointer.

Do not skip a module merely because the final report is shorter. A short report may omit low-value prose, but the underlying checks remain mandatory when the relevant data exists.

## Mandatory analytical order

### 1. Identify session and player
Confirm:
- track and track length;
- session type and race distance;
- application version and database schema;
- player car index, driver identity and team;
- official classification when available;
- setup and ERS profile actually used.

### 2. Audit capture quality
Before performance analysis, assess:
- missing/invalid packets;
- raw packet availability;
- packet_id=8 / final classification;
- queue drops or capture gaps;
- session-type mapping;
- player-only derived telemetry coverage;
- flashback handling;
- telemetry freshness where relevant.

### 3. Build an Analysis Validity Map
Every lap or session phase used in analysis must be classifiable as one of:
- `performance-valid`
- `racecraft-only`
- `strategy-only`
- `diagnostic`
- `excluded`

At minimum consider:
- compound;
- weather;
- fuel state;
- traffic;
- pit/outlap/inlap;
- damage;
- SC/VSC/formation state;
- flashback/debug activity;
- ERS profile/mode;
- user-declared constraints.

### 4. Segment the session
Do not average incompatible phases together. Split by the dimensions that materially change performance, especially:
- tyre compound;
- Full Wet vs Intermediate vs slick;
- normal running vs fuel saving;
- clean air vs battle/traffic;
- before/after setup damage;
- before/after pit stop;
- ERS profile/mode;
- Safety Car/VSC.

### 5. Analyse performance
Only after the validity map:
- clean pace;
- sector pace;
- theoretical lap;
- strategy;
- fuel;
- racecraft;
- corner performance;
- tyres;
- setup;
- ERS.

### 6. Produce measurable next actions
Recommendations must be testable. Prefer:
- a target segment time;
- a speed or input target;
- one setup change;
- one ERS rule change;
- one fuel target;
- a sample-size requirement.

Avoid vague recommendations such as "be smoother" unless they are tied to a measurable telemetry pattern.

## Hard comparison rules

- Do not compare Full Wet and Intermediate pace as if they were one population.
- Do not compare fuel-saving laps with normal performance laps.
- Do not compare pit/debug flashbacks with driving-error flashbacks.
- Do not compare corner times across sessions unless the segment boundaries are identical.
- Do not call a setup change an A/B result if more than one primary variable changed or the conditions are materially different.
- Do not build a theoretical lap from mutually incompatible conditions.
- Do not promote an ERS/setup candidate to baseline before live validation.

## Required quantitative standards

For every important conclusion, include the strongest available numerical evidence.

For problematic corners or segments, normally include:
- sample size;
- best;
- representative median;
- weak/battle pass;
- absolute loss;
- minimum and/or exit speed when explanatory;
- brake/throttle/steering pattern;
- yaw/rear slip/ERS when relevant;
- explicit "How to drive it" prescription.

For pace, include:
- sample definition;
- n;
- median;
- best;
- comparison benchmark;
- uncertainty/context.

For theoretical lap, include:
- compatible component definition;
- theoretical time;
- actual best;
- theory-to-actual gap.

## Root-cause labels

When possible classify a material loss using one or more of:
- `Entry`
- `Rotation`
- `Traction`
- `ERS`
- `Traffic`
- `Tyre`
- `Fuel`
- `Damage`
- `Strategy`
- `Data quality`

Do not stop at a symptom such as "slow exit" when telemetry supports a more specific cause.

## Confidence

Assign High / Medium / Low confidence to major conclusions where evidence quality differs.

Confidence should depend on:
- sample size;
- condition comparability;
- telemetry completeness;
- repeated evidence;
- contamination by traffic, fuel saving, damage, flashbacks, or changing weather.

One fast lap is not automatically High confidence.

## Wet-report convention

Use a separate wet series with report prefix `W`, for example:
- `W01 - Japan - 50% Wet Race - ...`

Dry development may continue as `R01`, `R02`, etc.

Wet and dry setup/ERS baselines should not silently overwrite each other.

## Deliverables

When the user requests a complete analysis:
1. full report;
2. concise summary of the main findings;
3. next-session measurable priorities;
4. required project-file updates;
5. ERS/setup candidate updates when justified;
6. application feature requirement when telemetry proves the current app cannot support a needed strategy.

Preserve history. Never delete old reports, sessions, setup history, or ERS validation records merely because a newer version exists.
