# F1 Telemetry Lab - C# MVP v0.5.3

## v0.5.3

Combined race-analysis release. Because apparently one release at a time was too emotionally stable.

### Race Report
- Added lap summary block: clean laps, best lap, average clean pace, tyre wear, fuel, ERS, pit laps and data-quality flags.
- Kept the real UI table with grouped columns, wrapping notes, column help and CSV export.

### Driver Compare
- New `Driver Compare` tab.
- Compare 2-3 drivers by lap number, stint lap or compound.
- Metric groups: Pace, Tyres, Fuel/ERS, Damage.
- Includes a simple lap chart for the selected metric group.

### Stint Report
- New `Stint Report` tab.
- Groups laps by compound changes, pit-stop flags and tyre-age resets.
- Shows stint length, best lap, average clean lap, rough degradation slope, tyre wear, fuel, ERS and damage.

### Pit Report
- New `Pit Report` tab.
- Detects pit / compound-change laps.
- Shows before/after compound, in/out laps, rough pit-loss estimate, wear, fuel and damage notes.

### Existing features kept
- Track Map remains.
- Track Detail remains hidden from the visible UI.
- Austria Racenet spline remains available for Track Map boundary rendering.
