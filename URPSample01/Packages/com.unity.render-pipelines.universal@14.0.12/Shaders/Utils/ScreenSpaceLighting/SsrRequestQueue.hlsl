// R1 records an unresolved opportunity, never radiance or a valid environment hit.
// reason: invalid, rough, reliable SS, missing history, trace miss, rejected history, edge.
#include "SsrRayRequest.hlsl"
RWStructuredBuffer<R1Sample> _R1Original, _R1Queue;
// B6.2: slot-indexed early screen written by the classify kernel, read by the per-pixel consumers.
RWStructuredBuffer<R1Coverage> _R1Coverage;
RWStructuredBuffer<uint2> _R1Tile; // count then exclusive base in rotated tile order
RWStructuredBuffer<uint2> _R1Mask; // wanted bit per pixel, 64 bits per tile
RWStructuredBuffer<uint> _R1Counts; // eligible, requested, served, overflow, then histogram
uint _R1K, _R1Phase;
float _SsrRayLength; // volume rayLength, already clamped to the camera far plane

uint2 R1Grid() { return ((uint2)_SsrTraceSize.xy + 7) / 8; }
uint R1TileId(uint2 g) { return g.y * R1Grid().x + g.x; }
// Even split of the fixed budget: with K seen from every tile the sliding tile window cannot
// leave a tail of tiles unserved until it wraps around.
uint R1Quota()
{
    uint tiles = max(1u, R1Grid().x * R1Grid().y);
    return max(1u, _R1K / tiles);
}
// An even split wastes the budget on tiles whose reflections are already fully resolved and
// starves the tiles where the screen-space trace missed (the reflection silhouettes). Every tile
// with requests keeps one slot, and the rest of the budget is shared by the wanted count, capped
// at the tile's own wanted count. The total never exceeds K, so the scatter budget check and the
// rotation stay exact; K <= tiles falls back to the even split so no tile starves outright.
// wanted is a tile count (<= 64), so the product below stays well inside 32 bits.
uint R1TileQuota(uint wanted, uint totalWanted)
{
    if (wanted == 0u)
        return 0u;
    uint tiles = max(1u, R1Grid().x * R1Grid().y);
    if (_R1K <= tiles)
        return min(R1Quota(), wanted);
    uint extra = _R1K - tiles;
    uint quota = 1u + (totalWanted > 0u ? (extra * wanted) / totalWanted : 0u);
    return min(quota, wanted);
}

#if defined(SSR_R1_HISTORY) || defined(SSR_R1_COLD)
// The reason histogram and the eligible/requested totals are folded per block first: the former
// per-pixel InterlockedAdd put every thread of the dispatch on the same seven counters in L2.
#define R1_REASON_COUNT 7
groupshared uint r1Reason[R1_REASON_COUNT];
groupshared uint r1Eligible, r1Requested;
// The block is exactly one 8x8 tile, so the per-pixel "wants a ray" verdict is accumulated here and
// published as a 64-bit mask. The tile pass and the scatter used to re-read the 80-byte sample just
// for that bit, at an 80-byte stride that wasted every cache line it touched.
groupshared uint r1BitLo, r1BitHi;
[numthreads(8,8,1)]
#if defined(SSR_R1_HISTORY)
void R1Original(uint3 id : SV_DispatchThreadID, uint gtid : SV_GroupIndex)
#else
void R1Cold(uint3 id : SV_DispatchThreadID, uint gtid : SV_GroupIndex)
#endif
{
    SSGI_ASSIGN_VIEW_INDEX(id.z);
    if (gtid < R1_REASON_COUNT) r1Reason[gtid] = 0;
    if (gtid == R1_REASON_COUNT) r1Eligible = 0;
    if (gtid == R1_REASON_COUNT + 1) { r1Requested = 0; r1BitLo = 0; }
    if (gtid == R1_REASON_COUNT + 2) r1BitHi = 0;
    GroupMemoryBarrierWithGroupSync();

    // Edge blocks overhang the trace size; those threads must stay alive for the barrier.
    if (!any(id.xy >= (uint2)_SsrTraceSize.xy))
    {
    uint2 src = SsrGetFullResCoord(id.xy);
    R1Sample s = (R1Sample)0;
    s.slot = id.y * (uint)_SsrTraceSize.x + id.x;
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
                float edge, share;
                bool usable;
#if defined(_GLX_SSR_SHADE_SS)
                // The radiance is shaded from this frame's GBuffer by SsrHitSs, so the
                // previous-colour pyramid sample has no consumer here. Its mip-selected gather, the
                // mip-adjusted border limit and the colour validity test only existed to validate
                // that sample; coverage is the material fade times the edge fade.
                uint2 hitPixel = (uint2)(saturate(hit) * _SsrScreenSize.xy);
                hitPixel = min(hitPixel, (uint2)_SsrScreenSize.xy - 1u);
                float2 prevFrameNDC = hit - SSGI_LOAD(_SsrMotionTexture, hitPixel).xy;
                // The same on-screen test the pyramid path applies at mip 0 (its limit is 1), minus
                // the mip border expansion: nothing is sampled from the pyramid here.
                usable = all(prevFrameNDC >= 0.0) && all(prevFrameNDC <= 1.0);
                edge = usable ? EdgeOfScreenFade(prevFrameNDC, _SsrEdgeFadeRcpLength) : 0.0;
                share = fade * edge;
#else
                bool accepted;
                SsrReadOriginalColor(hit,s.roughness,share,edge,accepted);
                usable = accepted && isfinite(edge);
#endif
                s.ssShare = usable ? share : 0.0;
                s.reason = 5;
                if (usable)
                {
                    // A hit on a depth silhouette is the least reliable sample, so its share goes
                    // back to the hardware fill (reason 6) instead of leaving a binary edge that the
                    // probe has to fill.
                    float silhouette = SsrHitEdgeConfidence(hit);
                    s.ssShare *= silhouette;
                    s.requestShare = fade * (1 - edge * silhouette); // never reclaim material roughness fade
                    s.reason = s.requestShare > 0 ? 6 : 2;
                }
            }
#endif
        }
    }
    // Only reason >= 2 can be read later: every consumer gates on the 12-byte coverage record
    // first, and the scatter ranks slots with requestShare > 0 only. Background, rough and invalid
    // pixels stop paying a 64-byte store each.
    if (s.reason >= 2u) _R1Original[s.slot] = s;
    R1Coverage cov;
    cov.ssShare = s.ssShare;
    cov.requestShare = s.requestShare;
    cov.reason = s.reason;
    _R1Coverage[s.slot] = cov;
    InterlockedAdd(r1Reason[s.reason],1);
    if (s.reason >= 2) InterlockedAdd(r1Eligible,1);
    if (s.requestShare > 0)
    {
        InterlockedAdd(r1Requested,1);
        if (gtid < 32) InterlockedOr(r1BitLo,1u<<gtid);
        else InterlockedOr(r1BitHi,1u<<(gtid-32));
    }
    }
    GroupMemoryBarrierWithGroupSync();

    // Publish the tile: the wanted mask the scatter ranks, and the count R1Offsets splits the budget by.
    if (gtid == 0)
    {
        uint2 bits=uint2(r1BitLo,r1BitHi);
        uint t=R1TileId(uint2(id.xy>>3));
        _R1Mask[t]=bits;
        _R1Tile[t]=uint2(countbits(bits.x)+countbits(bits.y),0);
    }
    if (gtid < R1_REASON_COUNT)
    {
        if (r1Reason[gtid] > 0) InterlockedAdd(_R1Counts[4+gtid],r1Reason[gtid]);
    }
    else if (gtid == R1_REASON_COUNT)
    {
        if (r1Eligible > 0) InterlockedAdd(_R1Counts[0],r1Eligible);
        if (r1Requested > 0) InterlockedAdd(_R1Counts[1],r1Requested);
    }
}
#endif

// One group per tile. The lane->pixel window advances by the tile's own quota, not by one, so
// consecutive frames serve disjoint pixel sets and a 64-pixel tile is fully covered in
// 64/quota frames. A stride of 1 made the window overlap itself 7/8 per frame, i.e. a full
// sweep took 64 frames and the fill visibly crawled.
groupshared uint r1Mask[64];
void R1Rank(uint2 tile, uint lane, out uint slot, out uint rank, out uint count)
{
    uint t = R1TileId(tile);
    uint2 bits = _R1Mask[t];
    // The window stride is the tile's own served count, so a weighted quota still advances
    // disjoint pixel sets per frame.
    uint quota = _R1Tile[t].x;
    uint order = (lane + quota * _R1Phase + t*17) % 64;
    uint2 coord = tile*8 + uint2(order%8,order/8);
    slot = coord.y*(uint)_SsrTraceSize.x+coord.x;
    // Pixels outside the trace size never set a bit, so the mask carries the bounds check too.
    uint wanted = ((order < 32u) ? (bits.x >> order) : (bits.y >> (order-32u))) & 1u;
    r1Mask[lane] = wanted;
    GroupMemoryBarrierWithGroupSync();
    rank=0; count=0;
    [unroll] for(uint i=0;i<64;++i) { count+=r1Mask[i]; if(i<lane) rank+=r1Mask[i]; }
    if(!wanted) rank=64;
}
// One block scans the rotated tile order: each lane owns a contiguous slice of the sequence, so the
// exclusive bases and the served total are exactly what the former single-lane loop produced. The
// sums are unsigned adds of non-negative values, so summation order cannot change any result.
// Scanning over positions in the rotated order (tile = rotate + position, wrapped) keeps the write
// targets identical instead of rotating the output afterwards.
#define R1_SCAN_LANES 1024
groupshared uint r1Scan[R1_SCAN_LANES];
[numthreads(R1_SCAN_LANES,1,1)]
void R1Offsets(uint3 id : SV_DispatchThreadID)
{
    uint lane=id.x;
    uint2 grid=R1Grid(); uint tiles=max(1u,grid.x*grid.y);
    // B6.1: quota is per tile now, shared by the frame's wanted count (written by R1Original).
    uint totalWanted=_R1Counts[1];
    uint chunk=(tiles+R1_SCAN_LANES-1)/R1_SCAN_LANES;
    uint begin=min(lane*chunk,tiles), end=min(begin+chunk,tiles);
    uint rotate=_R1Phase%tiles;
    uint local=0;
    for(uint p=begin;p<end;++p)
    {
        uint t=rotate+p; if(t>=tiles) t-=tiles;
        local+=R1TileQuota(_R1Tile[t].x,totalWanted);
    }
    r1Scan[lane]=local;
    GroupMemoryBarrierWithGroupSync();
    // In-place inclusive scan of the lane totals.
    for(uint stride=1;stride<R1_SCAN_LANES;stride<<=1)
    {
        uint add=lane>=stride?r1Scan[lane-stride]:0;
        GroupMemoryBarrierWithGroupSync();
        r1Scan[lane]+=add;
        GroupMemoryBarrierWithGroupSync();
    }
    uint total=r1Scan[R1_SCAN_LANES-1];
    uint base=r1Scan[lane]-local; // exclusive: the slice start in queue order
    for(uint q=begin;q<end;++q)
    {
        uint t=rotate+q; if(t>=tiles) t-=tiles;
        uint n=R1TileQuota(_R1Tile[t].x,totalWanted);
        _R1Tile[t]=uint2(n,base); base+=n;
    }
    // Degenerate K < tiles: the scatter bounds check drops whatever exceeds the budget.
    if(lane==0)
    {
        uint served=min(total,_R1K);
        _R1Counts[2]=served;
        _R1Counts[3]=_R1Counts[1]-served;
    }
}
[numthreads(64,1,1)]
void R1Scatter(uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    uint slot,rank,count; R1Rank(group.xy,lane,slot,rank,count);
    uint2 tile=_R1Tile[R1TileId(group.xy)];
    if(rank<tile.x && tile.y+rank<_R1K) _R1Queue[tile.y+rank]=_R1Original[slot];
}
