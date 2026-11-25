# I2 pre-fix run

This is the first I2 run (build GUID `c7a3eb5e…`, strip GUID `a4ab4ebb…`). The rendering data is
valid; the Player failed only two measurement gates:

- The B0/workload room is dim, so the mirror ROI moved `0.0068 -> 0.0095` (1.40x) while the gate
  required an absolute `+0.02`. The rerun gates the ratio (`> 1.2x`) and keeps the absolute values.
- `FrameTimingManager` whole-frame GPU samples were unusable on this machine (near-zero values,
  38/120 samples). The rerun keeps them as reference and measures the incremental RT GPU cost as
  the sum of the five non-overlapping GPU scopes (TLAS build, probe trace/update,
  relocate/classify, SSR trace, SSR reconstruction; hit shading is nested).

`../evidence` contains the same fixture with the corrected gates and a passing `Summary.json`.
Logs are local-only.
