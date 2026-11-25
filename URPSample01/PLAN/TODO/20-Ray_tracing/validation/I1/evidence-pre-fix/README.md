# I1 pre-fix run

This is the first I1 Player run. Its build GUID differs from `../evidence` because the fixture was
rebuilt with corrected gate strings; the authoritative run is `../evidence`.

The Player failed only on fixture gate strings, not on rendering data:

- `RayTracingRequests` is a `[Flags]` enum, so value 3 prints as `GI, Reflections`, not `3`.
- Replacing the probe producer resets its `updateCount`/light `uploadCount`; the per-stage delta is
  therefore negative or zero across a settings change. `generation` (per producer) is the correct
  one-batch-per-frame counter.

The JSON here is kept for audit. All numeric rows are valid; `../evidence` contains the same
fixture data with the corrected gates and a passing `Summary.json`. Logs are local-only.
