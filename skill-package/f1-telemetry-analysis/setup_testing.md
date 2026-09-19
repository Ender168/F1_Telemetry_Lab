# Setup testing and A/B methodology

## 1. Baseline vs candidate

Every setup conclusion must identify:
- current validated baseline;
- candidate;
- exact change;
- reason for the change;
- status: proposed / testing / validated / rejected.

Do not silently turn a candidate into the new baseline.

## 2. One primary change

A valid A/B test should change one primary variable or one tightly linked family.

Examples:
- front suspension 41 -> 40;
- on-throttle differential 100 -> 90;
- front wing 43 -> 45 with rear wing fixed;
- rear pressures 21.5 -> 21.0 as one tyre-pressure test.

If multiple major variables change, call it a new setup evaluation, not a clean A/B.

## 3. Hypothesis

Before the next test write:
- Problem
- Hypothesis
- Change
- Constants
- Success metrics
- Minimum useful sample

Example:
Problem: S1/front response weak in wet.
Hypothesis: +2 front wing improves turn-in without destabilising T14/T18.
Change: 43 -> 45 front wing.
Constants: rear wing 30 and mechanical setup unchanged.
Success: faster S1 median, no meaningful increase in T14/T18 slip, no tyre penalty.

## 4. Do not declare victory from contaminated evidence

An A/B result is weak when:
- weather differs materially;
- compound differs;
- fuel-saving contaminates one sample;
- damage differs;
- traffic differs severely;
- flashback/debug activity dominates;
- ERS profile changes simultaneously.

In these cases report direction only and schedule a cleaner retest.

## 5. Setup diagnosis by symptom

Use telemetry to connect changes to symptoms.

Examples:
- front response / S1: wing balance, front suspension, ARB, geometry;
- traction snap: differential, rear suspension, rear wing, ERS onset;
- kerb damage: ride height/suspension plus driving line;
- tyre overheating: pressure/alignment/load distribution.

Do not change setup merely because a generic setup guide recommends it.

## 6. Dry and wet baselines

Dry and wet setup development may diverge materially.

Do not overwrite a validated dry baseline with a wet setup.

Track them separately in history with clear condition labels.

## 7. Setup history update

After a meaningful test append:
- report ID;
- condition;
- setup identity;
- primary change;
- benchmark;
- result;
- verdict;
- next test.

Preserve old rows.

## 8. Recommended decision labels

- PASS: evidence supports adopting the candidate.
- PARTIAL: benefit exists but tradeoff/sample prevents adoption.
- FAIL: target did not improve or tradeoff dominates.
- INCONCLUSIVE: sample/conditions do not permit a verdict.
