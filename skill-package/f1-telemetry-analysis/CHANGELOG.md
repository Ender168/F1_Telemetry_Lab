# Changelog

## 2.0.0

Major standards update based on lessons from recent dry and wet race analyses.

### Added
- Validity-first analysis workflow.
- Analysis Validity Map with performance-valid / racecraft-only / strategy-only / diagnostic / excluded states.
- User-declared context as a hard analytical constraint.
- Automatic conceptual exclusion of fuel-saving phases from normal pace.
- Raw vs cleaned flashback classification.
- Separate wet report series using Wxx.
- Full Wet vs Intermediate separation.
- Fuel deficit onset and recommended start-fuel analysis.
- Fixed corner boundaries and numeric best/median/weak standard.
- Root-cause labels for corner losses.
- Mandatory "How to drive it" prescription for major problem corners.
- Top-3 loss focus instead of equal prose for every corner.
- Racecraft matched battle-vs-clear analysis.
- Tyre early/late wear slope and thermal acceleration analysis.
- Explicit setup A/B hypothesis / constants / success metrics.
- Candidate vs validated baseline lifecycle.
- ERS runtime identity and profile-selection verification.
- ERS traction-safe onset and post-onset analysis as separate checks.
- Traction waiting/audit/reset validation.
- Compound-aware Inter/Full Wet ERS architecture requirement.
- Application-limitation-to-feature-requirement workflow.
- Confidence labels for causal conclusions.

### Reorganized
- Split old setup/ERS logic into `setup_testing.md` and `ers_autopilot.md`.
- Added `data_quality_and_segmentation.md`.
- Added `corner_analysis.md`.
- Added `racecraft.md`.
- Added `tyres_and_conditions.md`.
- Kept `setup_and_ers.md` as a compatibility pointer.

### Preserved
- Cumulative track workspace.
- Historical reports and setup history.
- Full session, theoretical lap, racecraft, setup and ERS analysis.
- Drive/project updates after completed analysis.
- No deletion of old sessions/reports/profile versions.
