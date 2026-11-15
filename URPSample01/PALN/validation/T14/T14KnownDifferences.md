# T14 known differences and limitations

- URP has no per-material proxy volume: refraction uses the nearest box-projected reflection probe AABB, found through CullingResults.visibleReflectionProbes with a scene lookup fallback.
- Refraction samples the current opaque+sky color pyramid; HDRP's pre-refraction transparent ordering is not migrated, so transparents drawn after the opaque pass do not appear in refraction.
- SSGI runs its diffuse denoiser and consumes the denoised output; HDRP's raw SSGI return path is not reproduced (intentional, recorded since T6).
- The sample sequence uses IGN + R2 instead of HDRP blue-noise textures, so SSGI keeps a faint stipple at 1 spp after 32 warmup frames.
- Deferred consumes DepthNormals prepass material data, i.e. decal-deformed normals/decal color are not visible to SSGI/SSR tracing.
- Transparent SSR is Approximation-only and reprojects with hit-pixel and camera motion; URP 14 does not write transparent object motion, so moving transparent reflections can lag.
- GTAO and Screen Space Lighting own separate packed Hi-Z buffers; the duplicate depth-pyramid memory is accepted instead of a cross-feature broker.
- URP's Deferred compatibility path ignores MSAA, so the Deferred renderer does not promise MSAA support for these effects.
- URP 14.0.12 does not drive DynamicResolutionHandler: hardware dynamic resolution cannot rescale the RTHandle camera targets, so it is recorded as unsupported (static render scale is supported).
- APV is not integrated in URP 14.0.12, so probe-volume GI is marked unsupported; lightmap/SH/mixed lighting regresses green.
- The plugin keeps the _SCREEN_SPACE_GI/_SCREEN_SPACE_SSR/_SCREEN_SPACE_TRANSPARENT_SSR/_SCREEN_SPACE_REFRACTION variants in every build because URP's prefiltering data only covers URP keywords. Plugin-level build stripping was evaluated but not introduced in T14: it changes release behaviour and needs its own correctness cycle.
- Metal, Android/OpenGLES, macOS/iOS and physical HMDs are not tested in this environment and are not claimed as supported.
- One 'CommandBuffer: temporary render texture not found (SetComputeTextureParam)' warning is logged on the first frame after the effects are enabled on DX11; it predates T14 (present in the T12 capture logs) and does not affect the measured images, which are identical to the recorded T12 hashes.
