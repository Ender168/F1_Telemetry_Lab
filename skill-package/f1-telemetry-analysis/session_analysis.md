# Session analysis

Run this after data quality and segmentation.

## 1. Session card

Record:
- track;
- session type and length;
- date;
- player/team/car number;
- start and finish;
- official best lap;
- pit stops and strategy;
- penalties/warnings;
- raw and cleaned FLBK count;
- start/finish fuel when available;
- setup identity;
- ERS profile identity;
- app version;
- capture quality;
- analysis confidence.

## 2. Executive finding

The first analytical paragraph must answer:
- Was the result representative of underlying pace?
- What was the largest performance limiter?
- What was the largest technical/strategy limiter?
- What is the single most important next test?

Do not lead with a finishing position if strategy/traffic distorted it.

## 3. Race chronology

Describe major phases, not every lap:
- opening stint;
- traffic clusters;
- pit cycle;
- compound changes;
- damage;
- fuel-saving onset;
- closing phase.

This chronology should explain why different samples are or are not comparable.

## 4. Clean pace

For every clean-pace statement define the sample.

Include:
- compound/condition;
- lap range;
- n;
- median;
- best;
- sector medians when useful.

Benchmark hierarchy:
1. own comparable laps;
2. teammate in compatible conditions;
3. comparable cars/field;
4. previous sessions with identical conditions/segment definitions.

Do not rank the player from a tiny or contaminated sample.

## 5. Theoretical lap

Build theoretical laps only from compatible components.

Compatibility means, as applicable:
- same compound class;
- similar track condition;
- normal fuel regime;
- no major damage;
- no SC/VSC;
- no diagnostic branch;
- comparable setup/ERS state.

Report:
- component definition;
- theoretical time;
- actual best;
- theory-to-actual gap.

Interpretation:
- small gap: good lap assembly/reproducibility;
- large gap: speed exists, but components are not being combined.

Never assemble a theoretical lap from Full Wet S1 + Intermediate S2 + dry S3 and call it meaningful.

## 6. Strategy

Assess:
- tyre sequence;
- pit timing;
- undercut/overcut context;
- traffic rejoin;
- pit-stop loss where available;
- compound crossover;
- whether strategic position inflated or depressed the result.

Separate strategic success from clean pace.

## 7. Fuel analysis

For race sessions, include when data supports it:
- start fuel;
- representative kg/lap by meaningful regime;
- projected finish;
- first projected-deficit lap;
- actual behavioural saving onset;
- severity of lift/coast/clipping;
- recommended next start fuel.

Do not use fuel-saving laps as a normal pace baseline.

If the session mixes Wet and Inter, estimate consumption by regime rather than assuming one constant rate when the data clearly differs.

## 8. Damage and reliability

Report:
- damage onset;
- peak/end damage;
- whether it materially affects the sample;
- whether recurrent damage points to kerb/contact technique or setup sensitivity.

## 9. Progress versus history

Compare only metrics that are genuinely comparable.

Useful history:
- clean median;
- theoretical-to-actual gap;
- damage;
- cleaned FLBK;
- tyre limiter;
- known corner targets;
- ERS validation status.

If the metric is not comparable, state that instead of manufacturing a delta.

## 10. Handoff

Racecraft goes to `racecraft.md`.
Corner diagnosis goes to `corner_analysis.md`.
Tyres/conditions go to `tyres_and_conditions.md`.
Setup conclusions go to `setup_testing.md`.
ERS goes to `ers_autopilot.md`.
