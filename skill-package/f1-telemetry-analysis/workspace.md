# Workspace and cumulative project rules

The F1 telemetry workspace is cumulative. Treat each track folder as a long-running engineering notebook, not a disposable export folder.

## Expected track structure

A track folder should normally contain:
- `1_Setups/`
- `F1 Performance Reports/`
- `ERS Autopilot/` when ERS profiles are used
- historical session material and other track-specific supporting files as applicable

Do not delete older sessions, reports, validation records, or setup history.

## 1_Setups

`1_Setups` represents the current usable setup state for the track.

Rules:
- keep the current mechanical setup or baseline there;
- keep the current live ERS profile there when the workflow uses a track-level `<Track>_Race.json`;
- do not replace a validated baseline with an unvalidated candidate;
- candidate ERS profiles belong in `ERS Autopilot` until validation;
- candidate setup changes belong in setup history with an explicit status.

If a current live file such as `Japan_Race.json` is updated, preserve its filename when the application expects that filename, but update its internal profile revision/id appropriately.

## F1 Performance Reports

Store full session reports here.

Naming:
- dry/general series: `R01`, `R02`, ...
- wet series: `W01`, `W02`, ...

A wet report must remain distinguishable from the dry development chain.

Never overwrite an older report to make the folder look cleaner. History is data.

## ERS Autopilot

Store:
- historical profiles;
- current candidates;
- implementation specifications;
- validation records;
- pending validation files;
- compound-specific strategy specifications.

Recommended lifecycle:
1. hypothesis/specification;
2. candidate profile;
3. pending live-validation record;
4. live test;
5. PASS / PARTIAL / FAIL;
6. only then update the current live file in `1_Setups`.

If the application cannot implement a required strategy safely, store a specification and create/update an application issue rather than creating a fake "working" JSON.

## Historical continuity

Before analysing a new session, inspect previous reports/history for:
- previous benchmark pace;
- known problematic corners;
- setup changes;
- ERS revision and known limitations;
- tyre limiter;
- fuel target;
- unresolved issues;
- previous next-session priorities.

After analysis, explicitly state which previous issues:
- improved;
- persisted;
- worsened;
- became non-comparable.

## Setup history

The setup-history file is cumulative.

Each meaningful session should record, where available:
- report ID;
- date;
- track/session;
- weather/compound class;
- setup identity;
- setup values or change versus baseline;
- ERS profile identity;
- clean benchmark;
- result;
- tyre/fuel notes;
- flashback count, including cleaned count if diagnostics occurred;
- test verdict;
- next isolated change.

Do not use setup history as a dumping ground for every telemetry number. Store the engineering decision trail.

## Data provenance

The report should identify the primary source, for example:
`Suzuka_Race_YYYYMMDD_HHMMSS.rar -> session.sqlite`

Also record:
- app version;
- DB schema where available;
- capture-quality summary;
- profile/setup source.

## Preservation rule

When updating cumulative files:
- preserve unresolved issues;
- preserve historical entries;
- append or update the intended current-state field only;
- do not silently rewrite past conclusions using later knowledge.
