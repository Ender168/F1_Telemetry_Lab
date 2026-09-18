# Long-session review and v0.10.6

The supplied hour-scale recordings contain roughly 1.3 million UDP packets and more than 4.5 million per-car lap samples. Transport counters reported no queue drops. The first improvements therefore target post-recording work and live-control correctness, rather than increasing queue capacity.

## Implemented

| Finding | Change | Verification |
| --- | --- | --- |
| Lap quality retained the entire grid's sample history in managed memory | Read one session/car history at a time from indexed SQLite; reuse existing lap/flashback rules | Compare lap times, clean flags and sample counts with the reference analyzer across all 24 slots; exercise a suspected reset and player slot 21 |
| Live consumers decoded arrays to use only one car | Optional selected-car parser path; default still exposes all 24 slots | Selected-car results equal full-array results at slot 21; reject invalid indices |
| Thermal enrichment updated opponent rows that compaction immediately removed | Player-only enrichment during finalization, with a separate cache-scope marker | Existing thermal-data regression suite; all-player analysis fixtures |
| Final classification counts accumulated repeated packet 8 deliveries | Count the selected official classification; allow a packet-8-only session; retain UID isolation | Seven repeated packets yield 22 drivers; another session's results cannot become official for the selected lap session |
| Required finalization ran after replacing the source and swallowed failures | Enrich, compact, record analysis and verify integrity on the staging database | Inject a finalization failure and verify the original database's SHA-256 is unchanged |
| Session counters allocated SQL commands and could cross a packet's commit boundary | Reuse the command in the raw packet's transaction | Raw and segment counts agree at a batch boundary |
| Queued telemetry was evaluated as if its receive time were the current time | Live mode uses an injectable wall clock; dry-run replay retains packet time | Stale queued packets cannot send commands |
| Key release depended on further UDP traffic; Stop could drain commands before disabling input | Independent pulse-release timer; serialize stop with processing and stop input before queue draining | Stop blocks queued commands; Windows key-up needs an in-game smoke test |
| Changing numerical reasons produced excessive decision audit rows | Record semantic transitions and a one-second heartbeat; retain commands/feedback | A hundred distance changes produce one steady-state decision row, followed by the heartbeat |
| Repackaging deleted the previous archive before validating its replacement | Create a temporary RAR, verify it, then replace; drain process output streams concurrently | Inject a RAR test failure and verify the previous archive remains intact |

## Validation and limits

Local .NET 10 validation: 71 regression tests and 21 protocol/self-tests passed. The existing optional 72,000-packet fixture was explicitly enabled. That fixture contains one-car telemetry and uses a fake RAR runner; it is a functional load check, not a benchmark of the supplied full-grid races or real WinRAR compression. No real-session speedup percentage is claimed.

Raw UDP retention and the 24-slot parser contract are preserved. Windows `SendInput` behavior must still be checked with the game in the foreground, including stopping recording and losing UDP traffic during a pulse. This pull request does not change ERS strategy rules.

## Next priorities

1. Replay a complete private race on the same machine before/after the change and measure analysis time, peak memory, temporary disk usage and actual RAR time independently.
2. Correct race-profile learning eligibility for SC/VSC, wet laps and comparable tyre/stint conditions. Existing learned models may contain observations from unsuitable laps; this change does not repair them.
3. Investigate suspected counter resets against raw FLBK evidence before changing lap-quality rules. Backwards counters alone remain insufficient to label a rewind.
4. Profile UI refresh and report queries before restructuring MainWindow or adding caches. Avoid mixing those changes into the storage/control fixes.

Private race archives and raw telemetry are not repository fixtures.
