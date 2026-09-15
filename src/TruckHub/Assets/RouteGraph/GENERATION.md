# Route graph - how it was generated

Generated 2026-08-30 from the same `truckermudgeon/maps` parser output already used for the map
tiles (`F:\Claude Projects\_devtools\truckermudgeon-maps\out\parser\`) - no additional game-file
extraction needed, no truckermudgeon `generator` step involved for this part at all.

## Scripts

`F:\Claude Projects\_devtools\truckermudgeon-maps\extract-prefab-curves.ts` and
`extract-road-curves.ts` (run first, via `tsx` since both import truckermudgeon's own curve-geometry
code directly - dev-time only, never shipped, same GPL-3 boundary already accepted for the parser
dependency itself: only these steps' plain-JSON *output* feeds the graph that actually gets
embedded), then `F:\Claude Projects\_devtools\truckermudgeon-maps\build-route-graph.mjs` (not part
of truckermudgeon's own code - a separate, clean-room script written specifically for this).

```
npx tsx extract-prefab-curves.ts out/parser out/route-graph/prefab-connections.json
npx tsx extract-road-curves.ts out/parser out/route-graph/road-curves.json
node build-route-graph.mjs out/parser out/route-graph
```

Reads `usa-nodes.json`, `usa-roads.json`, `usa-prefabs.json`, `usa-companies.json`,
`usa-roadLooks.json`, and `prefab-connections.json`/`road-curves.json` (from the two steps above).
Builds a directed graph from two sources:

- **Roads**: direction comes from each road's own `roadLookToken`, resolved against
  `usa-roadLooks.json`'s `lanesLeft`/`lanesRight` arrays. A divided highway's two physical
  carriageways are each their own road item - one look has lanes only in `lanesRight` (forward,
  start→end), the other only in `lanesLeft` (backward, end→start) - same convention truckermudgeon's
  own generator uses internally (`packages/clis/generator/graph/graph.ts`). The original v1 treated
  every road as bidirectional regardless, which let A* hop onto the wrong-direction carriageway
  wherever it was momentarily cheaper - confirmed live as the cause of the drawn route visibly
  alternating between the two sides of a divided highway. 77,883 of 218,184 roads turned out to be
  one-way in one direction; 4 the other way; 656 roads had a look with no lanes recorded on either
  side (a data anomaly, not a real "impassable" signal) and were kept bidirectional rather than
  silently dropped.

  Each road's *shape* comes from `extract-road-curves.ts`, using the exact same cubic Hermite spline
  (`toSplinePoints`, node position + rotation/tangent at each end) truckermudgeon's own map renderer
  uses to draw these very roads in the base tiles - confirmed live as necessary: a straight line
  between a road's two endpoint nodes (the original v1 approach) visibly cut inside real sweeping
  curves the base map tiles already render correctly, since 14,984 of 218,184 roads have a real
  length notably longer (some 10%+) than the straight-line distance between their endpoints. 100,842
  of 218,184 roads turned out to have at least some real curvature (most roads bend at least a
  little; only genuinely dead-straight ones collapse back to the old single edge). Curve interior
  points become synthetic nodes shared by both directions of travel (only the edge order differs).
- **Prefabs** (intersections, roundabouts, and critically every company's own driveway-connector
  prefab): originally a full clique of straight-line edges between a prefab's boundary nodes - this
  was confirmed live as the cause of the drawn route cutting straight diagonal chords through
  interchanges instead of following the curved ramp. Now primarily uses the *real* traced lane
  geometry from `extract-prefab-curves.ts` - the same `navCurves` data ATS's own in-game AI traffic
  follows for that junction, via truckermudgeon's `calculateLaneInfo`/`toMapPosition`. Each curve's
  interior points become synthetic nodes (no uid of their own) chained by short directed edges, so
  the route actually follows the ramp instead of cutting through the middle of it.

  Curve data alone isn't a complete connectivity guarantee, though - it's the AI-traffic nav mesh,
  which can omit a maneuver a real vehicle could still physically make even though AI traffic never
  needs to make it. Confirmed live: a 2684km cross-country job that routed fine on the old
  straight-line-clique graph came back "no route found" once prefabs went curve-only - across a
  route that long, it only takes one prefab along the way where curve tracing didn't cover the
  needed direction to break the whole thing. Fixed by falling back to any ordered boundary-node pair
  within a prefab that the real curves didn't already cover (139,406 of them, against 1,880,163 real
  curve edges - about 7% of prefab edges, filling gaps rather than replacing the curve data). This
  restored connectivity to the original clique baseline while still following real curve geometry for
  the large majority of turns.

  **2026-09-08 attempt, reverted 2026-09-09**: tried replacing that straight chord with a cubic
  Hermite spline (same technique `extract-road-curves.ts` uses for curved roads - each boundary
  node's own `rotation` as tangent), after a screenshot showed a drawn route visibly cutting a corner
  at a real I-70 interchange. **This was wrong, not just imperfect, and was reverted the next day
  after real drive screenshots showed it.** A road's own start/end rotations are guaranteed (by the
  game's own road authoring) to point roughly along the chord between them - that's exactly what
  makes Hermite fitting well-behaved for curved roads. Two arbitrary boundary nodes of the *same*
  prefab carry no such guarantee: each one's rotation is just that node's own connecting road's
  heading, with no required relationship to the other node or to the chord between them. Hermite
  fitting with uncorrelated/near-reverse/near-perpendicular tangents is a known way to get overshoot -
  loops, cusps, sharp zigzag spikes - and that's exactly what showed up live, in ordinary in-town
  intersections nowhere near an interchange (worse than the corner-cutting it was meant to fix, not
  better). Back to the plain straight chord: visually imperfect at the interchanges curve tracing
  doesn't cover, but geometrically bounded - a straight line can cut a corner, it can never loop back
  on itself. 139,406 fallback edges (ATS) / 150,165 (ETS2), matching the original pre-attempt figures
  exactly. If this gets revisited, the real fix isn't a different curve-fit formula over the same
  tangent data - the boundary-node rotations just aren't the right signal for this - it'd need
  something that respects both endpoints' actual geometry (e.g. only spline pairs where the heading
  difference is already small, straight-chord everything else) rather than applying one curve-fit
  approach universally.

Company destinations resolve via each company's own `prefabUid`, not `company.nodeUid` directly -
investigation showed companies connect to the graph through a small dedicated prefab, not straight
to a road. When a company's own prefab has multiple candidate boundary nodes, the one already in the
main connected component (see pruning below) is preferred over an arbitrary first pick - needed once
prefab connectivity became directional, since a company's driveway can have an exit-only or
entry-only boundary node that would otherwise force an avoidable nearest-node snap.

A small number of nodes' forward/backward item chains reference item types the parser doesn't
expose at all (checked every category it extracts and found nothing), leaving tens of thousands of
separate weakly-connected components after building all road+prefab edges - most of them tiny
orphaned fragments, a handful of nodes each. Confirmed live: a runtime nearest-node search
occasionally landing on one of these fragments (for the truck's own position, or a manually-clicked
map point) was the actual cause of "no route found" for perfectly reasonable inputs, not the search
algorithm. **The graph is pruned down to just the single largest connected component before
shipping.** This is *weak* (undirected) connectivity, deliberately, not full strong connectivity -
strong connectivity was tried first, on the theory that a directed A* needs every node to actually
reach every other, but that's the wrong bar: real routing (this feature and every mainstream
GPS/mapping engine alike) only ever needs a path from one specific start to one specific
destination, not mutual reachability between all pairs. Requiring strong connectivity pruned the
graph down to 62% of nodes and dropped company resolution to 1498/2096 - it was discarding entirely
normal one-way topology (on-ramps and one-way spurs with no reverse edge, which is correct and
expected, not a data gap) as if it were the same orphaned-fragment problem this pruning was
originally added for. Weak connectivity catches the original bug while accepting that a specific
(start, destination) pair may occasionally still have no directed path - already handled gracefully
at runtime (empty route, logged, same as any other "no route found"). Any destination whose own
resolved node falls outside the main component gets snapped to the nearest one that's in it, within
300m. Result: 2093 / 2096 companies resolved (99.86%), only 3 unrecoverable.

## Result

**ATS**: 2,187,517 routable nodes (357,874 real graph nodes plus ~1.5M synthetic prefab-curve points
and ~535K synthetic road-curve points), 2,926,055 directed edges. `nodes.bin` 16.7MB + `edges.bin`
44.6MB + `companies.json` 147KB = 61.5MB uncompressed, 43.1MB zipped.

**ETS2**: 2,847,181 routable nodes, 3,732,302 directed edges. `nodes.bin` 21.7MB + `edges.bin`
57.0MB + `companies.json` 123KB = 78.8MB uncompressed, ~51.2MB zipped.

Both up from the original straight-line version's ~7.8MB zipped - the cost of shipping real curve
geometry for roads and prefabs instead of just their endpoints/boundary nodes. Worth knowing if the
embedded app size becomes a concern later.

## Format

- `nodes.bin`: one `(x: float32, z: float32)` pair per node, raw ATS world coordinates (not yet
  projected - convert with `GpsProjectionService.ToLonLat` only when producing final output points).
- `edges.bin`: one `(fromIndex: uint32, toIndex: uint32, weight: float32, heading: float32)` per
  directed edge. `weight` is estimated travel TIME in seconds (segment distance / speed), not raw
  distance - roads use that state's real truck speed limit for the road's look-derived speed class
  (`usa-countries.json`'s `truckSpeedLimits`, same `getLaneSpeedClass` classification truckermudgeon's
  own generator uses); junction-internal edges (prefab curves and fallback edges) use a flat
  conservative local-road speed rather than a full per-node country+class lookup, since they're short
  relative to a whole route. Switched from raw distance after a real job showed the router preferring
  a longer, winding back-road path over the interstate the in-game GPS actually took - shortest
  *distance* isn't the same as fastest, and the in-game GPS clearly optimizes for the latter.
  `RoutingService.cs`'s A* heuristic was updated to match (straight-line distance / an optimistic top
  speed, not raw distance) since a heuristic's units have to match the weights it's compared against.
  `heading` is `atan2(dz, dx)` in radians - not currently used for turn-penalty scoring in the C#
  router, but packed in case that's added later.
- `companies.json`: array of `{ cityToken, companyToken, nodeIndex }` - `nodeIndex` is an index
  into `nodes.bin`'s implicit array, already resolved at pack time (no runtime UID lookups needed).

## Verified with standalone Node tests

`test-route.mjs` (same `_devtools` folder) - a throwaway A* implementation (naive array-sort
priority queue, not representative of runtime performance) used only to sanity-check the graph and
algorithm design before porting to C#. Confirmed plausible road-following paths (e.g. a real
16.3km-straight-line trip came back as an 18.4km road route, up slightly from 18.0km pre-one-way-fix
now that wrong-direction carriageways are excluded) - the real C# implementation uses
`System.Collections.Generic.PriorityQueue` and should be dramatically faster than this test.

`test-route-batch.mjs` - same toy A*, run across many random company pairs rather than one, to
measure the realistic "no directed path found" rate on the now-genuinely-directed graph rather than
judging off a single pair (see the weak-vs-strong-connectivity discussion above).

## Re-running later

Re-run both scripts (in order: `extract-prefab-curves.ts` then `build-route-graph.mjs`) whenever the
tile data is regenerated (new DLC, etc.) - same parser output, no separate game-file access needed.
Bump the app version so the extraction cache picks up the change.

## Re-generated for ATS 1.61 (2026-09-15)

Re-run alongside the sibling `Assets/WebMap` regeneration (see its own `GENERATION.md` for the
ATS 1.61 Idaho speed-limits parser fix that this depended on - the same crash blocked this graph
build too, since both read `usa-*` parser output from the same `out/parser` run). ATS only; ETS2's
`europe-*` parser output and `ets2-route-graph.zip` were never touched.

Straight-chord prefab fallback confirmed still in effect (not the 2026-09-08 Hermite-spline attempt
that was reverted the next day - see above): the live `build-route-graph.mjs` source has no spline
code in the fallback-edge path, only historical comments documenting the revert.

Before -> after (pre-1.61 baseline vs. this run):

- `extract-prefab-curves.ts`: 366569 -> 368495 curved connections.
- `extract-road-curves.ts`: 218184 -> 219176 road curves (976029 total points).
- `build-route-graph.mjs`: 139406 -> 140031 fallback straight-line edges (prefab boundary-node pairs
  curve tracing didn't cover - same order of magnitude, expected for new/changed prefabs in the
  updated map data). One-way roads: 78127 forward-only / 4 backward-only (was 77883/4), 656 kept
  bidirectional (no lanes recorded either side - data anomaly, not new).
- Result: **2195118 routable nodes / 2935795 directed edges** (was 2187517 / 2926055).
  `nodes.bin` 17.6MB (16.7MB reported by the script, filesystem shows 17,560,944 bytes) +
  `edges.bin` 46.97MB + `companies.json` 151KB = ~61.7MB uncompressed, 41.3MB zipped (`route-graph.zip`,
  flat `nodes.bin`/`edges.bin`/`companies.json` entries at the zip root, matching
  `RoutingGraph.cs`'s `archive.GetEntry(...)` lookups).
- Company resolution: 2107/2114 resolved via own prefab / snapped-to-main-network (99.67%, was
  2093/2096 = 99.86% - company count itself grew 2096 -> 2114 with the 1.61 update).
