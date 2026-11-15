# SSR/SSGI ghosting while dragging the view (2026-11-16)

## Symptom

Dragging the view in the editor left ghost reflections trailing behind the objects. Reported after the
ray-origin fix (`validation/SsrRayOriginFix`), so the question was whether a temporal state (motion
vectors, history validation, pyramid capture) was still wrong.

## Reproduction

Baseline scene, `SslT7ForwardPlusRenderer`, ground forced to a metallic mirror, Forward+, 1280x720.
The sequence runs across editor update ticks so `Time.frameCount` and the motion-vector history advance
like they do in the editor:

1. warm up 60 frames at the base orientation,
2. yaw 3 degrees per frame for 4 frames,
3. stop the camera and keep rendering,
4. reset history and converge 60 frames at the final orientation.

Each config renders the SSR and SSGI in isolation. Differences are measured against the converged
frame at the same orientation.

| Config | Motion texture | During drag vs converged | One frame after stop vs converged |
|---|---|---|---|
| SSR only | meanAbs (0.0368, 0.0063), max 0.0615 | mean 0.0056, 81,273 px > 0.02, max 27.5 | **0 px** (max 0.0118) |
| SSGI only | same | mean 0.0003, 3,971 px > 0.02, max 0.49 | 387 px > 0.02 |
| Both | same | mean 0.0059, 85,287 px > 0.02 | 408 px > 0.02 |

`MotionVectors.png` is the dumped `_MotionVectorTexture` during the drag, `SsrDuringDrag.png` /
`SsrConverged.png` are the SSR-only images, `SsrDuringDragDiff_x8.png` is the 8x amplified difference.

## Findings

- The motion vectors are valid and carry the camera rotation (3 degrees of yaw produces ~0.037 UV of
  mean forward motion).
- SSR has no temporal history of its own and **recovers in exactly one frame**: the first frame after
  the camera stops is byte-identical to the converged image. The remaining drag artifact is the
  previous-frame color pyramid itself, which is what HDRP uses as well
  (`GetPreviousFrameRT(HDCameraFrameHistoryType.ColorBufferMipChain)` in `HDRP.ScreenSpaceReflection`);
  there is no way to read the current frame's shaded color from inside the pre-opaque SSR pass.
- The artifact is amplified by the color pyramid feedback: the captured pyramid already contains the
  previous frame's SSR result, so a high-weight (mirror) surface can reflect itself through a chain of
  pixels. A moving camera breaks that chain and the difference becomes visible; a still frame rebuilds
  it within one frame. HDRP captures its pyramid the same way (after opaque lighting, before final
  transparency), so this is structural, not a porting defect.
- SSGI is the only effect with a decaying residual: its history accumulates at 0.93 and a purely
  rotating camera keeps the history valid (the surface does not disocclude), so the pre-drag GI
  survives for several frames after the drag stops. HDRP's `TemporalFilter` validation has the same
  position/previous-depth rules; the port deliberately omits the history-normal comparison and the
  object-motion stencil rejection (documented in `TemporalFilter.compute`).

## Options for the residual artifact

1. **PBR accumulation (HDRP's artifact-managed path, already implemented).** Jittered GGX samples are
   temporally accumulated, and `speedRejectionParam` rejects the history while the receiver or the hit
   moves. With the drag measured here the local speed saturates, so history is rejected during motion
   (no drag trail) and accumulation resumes when the camera stops.
2. **Approximation motion handling (not HDRP, needs a product decision).** Bias the color pyramid mip
   by the receiver/hit motion so stale highlights become a blur instead of a sharp trail, or fade the
   SSR weight towards the probe while the motion is large. Both trade reflection sharpness or strength
   during camera motion for a less visible artifact.
3. **SSGI history rejection.** Comparing the current and history normals (HDRP's
   `MAX_NORMAL_DIFFERENCE` rule) needs a history normal texture that the SSL history does not keep;
   the effect would be a faster reset of GI on rotation.

## Evidence

`SslGhostDiag.txt` (metrics), `UnityGhostDiagFrameStepped.log` (full run),
`MotionVectors.png`, `SsrDuringDrag.png`, `SsrConverged.png`, `SsrDuringDragDiff_x8.png`.