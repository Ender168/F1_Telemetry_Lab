# Data quality, validity and segmentation

This module must run before pace, corner, setup, tyre or ERS conclusions.

## 1. Capture audit

Check, when available:
- session UID consistency;
- track_id and track length;
- session type;
- player car index;
- number of cars;
- packet coverage;
- packet_id=8 final classification;
- raw packet availability;
- derived-table coverage;
- queue drops;
- estimated missing frames;
- app version;
- DB schema;
- 2026 packet mapping;
- rewind/FLBK events;
- final classification quality: official vs reconstructed.

Classify capture quality:
- Good
- Usable with limitations
- Poor

State the limitation and what it prevents.

## 2. User-declared context is evidence

Treat explicit user context as a hard constraint unless telemetry disproves it.

Examples:
- critical fuel saving began on L19;
- many pit flashbacks were diagnostic;
- a controller issue affected inputs;
- a setup parameter was intentionally held constant;
- a pit-stop bug was being reproduced.

Verify timing/telemetry where possible.

Never bury such context as a footnote if it changes the valid sample.

## 3. Analysis Validity Map

Create a lap/phase map conceptually or explicitly.

Recommended fields:
- Lap / phase
- Compound
- Weather
- Fuel state
- Traffic state
- Pit state
- Damage state
- SC/VSC
- FLBK/debug
- ERS profile/mode
- Validity class
- Exclusion reason
- Notes

Validity classes:

### performance-valid
Usable for clean pace, sectors, theoretical lap and corner benchmarks.

### racecraft-only
Not clean enough for base pace, but useful for battle/traffic analysis.

### strategy-only
Useful for fuel, pit, tyre strategy, energy or race chronology, but not normal pace.

### diagnostic
Generated or materially altered by bug hunting, application testing, repeated pit attempts or similar diagnostic activity.

### excluded
Do not use for performance conclusions.

A lap may be valid for one question and invalid for another. Do not force one global "clean" flag when the evidence is more nuanced.

## 4. Hard exclusion triggers

Normally exclude from base performance:
- pit in/out laps unless specifically analysing pit performance;
- SC/VSC/formation-lap running;
- obvious flashback-contaminated branches;
- severe damage laps;
- critical fuel-saving/clipping phase;
- incompatible compound/weather;
- invalid timing;
- laps with major incidents.

Do not automatically exclude battle laps from racecraft analysis.

## 5. Session segmentation

Create phases based on actual state changes.

Important dimensions:
- slick / Intermediate / Full Wet;
- weather intensity;
- fresh vs worn tyre phase;
- clean air vs traffic/battle;
- normal fuel vs fuel saving;
- damage onset;
- pit cycle;
- ERS profile/mode;
- Safety Car/VSC.

Do not merge phases merely to increase sample size.

## 6. Fuel-deficit onset

For race sessions, estimate projected fuel sufficiency where data allows.

Detect:
- actual fuel remaining;
- representative kg/lap in the current valid regime;
- laps remaining;
- projected requirement;
- projected deficit/surplus;
- first lap where deficit becomes material;
- first lap where driving changes due to saving.

If critical saving begins at lap N:
- L<N may remain normal pace;
- L>=N becomes strategy-only for fuel and possibly racecraft;
- L>=N must not be used as normal pace/ERS calibration unless explicitly justified.

Report the difference between:
- mathematical projected-deficit onset;
- behavioural fuel-saving onset.

## 7. Flashback classification

Do not report only a raw FLBK count.

Classify, when evidence allows:
- driving-related;
- pit/debug diagnostic;
- app-validation;
- uncertain.

Report:
- raw FLBK;
- excluded diagnostic FLBK;
- cleaned driving-relevant FLBK;
- locations/laps of meaningful clusters.

Do not compare raw counts between sessions when one session contains diagnostic rewinds.

## 8. Damage contamination

Track damage onset and severity:
- wings;
- floor;
- diffuser;
- sidepods/body where available.

A lap after damage may still be useful, but not as a clean setup A/B comparison against an undamaged lap without qualification.

## 9. Confidence framework

Use:
- High: repeated, clean, compatible evidence;
- Medium: useful but limited sample or mild contamination;
- Low: single/weak example or substantial condition mismatch.

State confidence next to major causal conclusions when it helps prevent overclaiming.
