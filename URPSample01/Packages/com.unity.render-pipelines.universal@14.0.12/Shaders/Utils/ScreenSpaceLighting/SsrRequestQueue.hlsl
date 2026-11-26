// R1 records an unresolved opportunity, never radiance or a valid environment hit.
// reason: invalid, rough, reliable SS, missing history, trace miss, rejected history, edge.
#include "SsrRayRequest.hlsl"
RWStructuredBuffer<R1Sample> _R1Original, _R1Queue;
RWStructuredBuffer<uint2> _R1Tile; // count then exclusive base in rotated tile order
RWStructuredBuffer<uint> _R1Counts; // eligible, requested, served, overflow, then histogram
uint _R1K, _R1Phase;
float _SsrRayLength; // volume rayLength, already clamped to the camera far plane

#if defined(SSR_R1_HISTORY) || defined(SSR_R1_COLD)
[numthreads(8,8,1)]
#if defined(SSR_R1_HISTORY)
void R1Original(uint3 id : SV_DispatchThreadID)
#else
void R1Cold(uint3 id : SV_DispatchThreadID)
#endif
{
    SSGI_ASSIGN_VIEW_INDEX(id.z);
    if (any(id.xy >= (uint2)_SsrTraceSize.xy)) return;
    uint2 src = SsrGetFullResCoord(id.xy);
    R1Sample s = (R1Sample)0;
    s.slot = id.y * (uint)_SsrTraceSize.x + id.x;
    s.sourcePixel = src.y * (uint)_SsrScreenSize.x + src.x;
    s.sequenceFrame = _SsrFrameIndex; // original sequence key = trace coordinate + frame
    // Opaque prepass has no material/instance identifier. Never fabricate one.
    s.instanceId = s.materialId = 0xffffffffu;
    s.tMin = 0.002; s.tMax = _SsrRayLength;
    float d = SSGI_LOAD(_SsrDepthTexture,src);
    float4 n = SSGI_LOAD(_SsrNormalTexture,src);
    bool validMaterial = SsrDecodeMaterial(n,s.normal,s.roughness);
    if (IsFinite(d) && d >= 0 && d <= 1 && !SsgiIsBackgroundDepth(d) && all(isfinite(n)) && validMaterial)
    {
        float fade = PerceptualRoughnessFade(s.roughness,_SsrRoughnessFadeRcpLength,_SsrRoughnessFadeEndTimesRcpLength);
        s.position = SsrReconstructPositionWS(src,d);
        float3 v = normalize(SSL_SSR_CAMERA_POSITION_WS - s.position);
        // Compare in the prepass encoding: decoding exactly 0.825f rounds
        // one ULP above 0.35f. The frozen upper endpoint is inclusive.
        if (n.a < 0.825f || s.roughness > _SsrRoughnessFadeEnd || fade <= 0) s.reason = 1;
        else if (all(isfinite(s.position)) && dot(v,s.normal)>0 && SsrOriginalDirection(id.xy,s.normal,v,s.roughness,s.direction))
        {
            s.reason = 3; s.requestShare = fade;
#if defined(SSR_R1_HISTORY)
            float2 hit = SSGI_LOAD(_SsrHitPointTextureRW,id.xy);
            s.reason = 4;
            if (all(hit >= 0) && all(hit <= 1))
            {
                float edge; bool accepted;
                SsrReadOriginalColor(hit,s.roughness,s.ssShare,edge,accepted);
                s.reason = 5;
                if (accepted && isfinite(edge))
                {
                    s.requestShare = fade * (1-edge); // never reclaim material roughness fade
                    s.reason = s.requestShare > 0 ? 6 : 2;
                }
            }
#endif
        }
    }
    _R1Original[s.slot] = s;
    InterlockedAdd(_R1Counts[4+s.reason],1);
    if (s.reason >= 2) InterlockedAdd(_R1Counts[0],1);
    if (s.requestShare > 0) InterlockedAdd(_R1Counts[1],1);
}
#endif

uint2 R1Grid() { return ((uint2)_SsrTraceSize.xy + 7) / 8; }
uint R1TileId(uint2 g) { return g.y * R1Grid().x + g.x; }
// Even split of the fixed budget: with K seen from every tile the sliding tile window cannot
// leave a tail of tiles unserved until it wraps around.
uint R1Quota()
{
    uint tiles = max(1u, R1Grid().x * R1Grid().y);
    return max(1u, _R1K / tiles);
}
// One group per tile. The lane->pixel window advances by the tile's own quota, not by one, so
// consecutive frames serve disjoint pixel sets and a 64-pixel tile is fully covered in
// 64/quota frames. A stride of 1 made the window overlap itself 7/8 per frame, i.e. a full
// sweep took 64 frames and the fill visibly crawled.
groupshared uint r1Mask[64];
void R1Rank(uint2 tile, uint lane, out uint slot, out uint rank, out uint count)
{
    uint2 grid = R1Grid(); uint t = R1TileId(tile);
    uint order = (lane + R1Quota() * _R1Phase + t*17) % 64;
    uint2 coord = tile*8 + uint2(order%8,order/8);
    slot = coord.y*(uint)_SsrTraceSize.x+coord.x;
    uint wanted = 0;
    if (all(coord < (uint2)_SsrTraceSize.xy)) wanted = _R1Original[slot].requestShare > 0;
    r1Mask[lane] = wanted;
    GroupMemoryBarrierWithGroupSync();
    rank=0; count=0;
    [unroll] for(uint i=0;i<64;++i) { count+=r1Mask[i]; if(i<lane) rank+=r1Mask[i]; }
    if(!wanted) rank=64;
}
[numthreads(64,1,1)]
void R1Tiles(uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    uint slot,rank,count; R1Rank(group.xy,lane,slot,rank,count);
    // Store the full count; R1Offsets decides how many the budget can grant.
    if(lane==0) _R1Tile[R1TileId(group.xy)] = uint2(count,0);
}
[numthreads(1,1,1)]
void R1Offsets()
{
    uint2 grid=R1Grid(); uint tiles=grid.x*grid.y; uint quota=R1Quota(); uint total=0;
    for(uint i=0;i<tiles;++i)
    {
        uint t=(_R1Phase%tiles+i)%tiles;
        uint n=min(quota,_R1Tile[t].x);
        _R1Tile[t]=uint2(n,total); total+=n;
    }
    // Degenerate K < tiles: the scatter bounds check drops whatever exceeds the budget.
    _R1Counts[2]=min(total,_R1K);
    _R1Counts[3]=_R1Counts[1]-min(total,_R1K);
}
[numthreads(64,1,1)]
void R1Scatter(uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    uint slot,rank,count; R1Rank(group.xy,lane,slot,rank,count);
    uint2 tile=_R1Tile[R1TileId(group.xy)];
    if(rank<tile.x && tile.y+rank<_R1K) _R1Queue[tile.y+rank]=_R1Original[slot];
}
