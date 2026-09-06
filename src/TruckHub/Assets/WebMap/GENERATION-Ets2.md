# GPS map data - ETS2 (dormant, not yet activated)

Generated 2026-09-01 against a legitimately-owned ETS2 install with a full DLC set (115 .scs files -
`E:\Steam\steamapps\common\Euro Truck Simulator 2`).

**Status: embedded, not live.** All the data described here is built and shipping inside the exe,
and the C# service layer (`GameMapProfile`, `Ets2ProjectionService`, `RoutingGraph.Load(SimGame)`,
`WebMapAssetExtractor`) can already load and use it - but `GameMapProfile.Ets2Enabled` is `false`,
so none of it is reachable from any live code path yet. This was built ahead of time on explicit
request ("program it but not activate it, so once [ATS is validated] we can just flip a switch") -
see [[project_truckhub_gps_map]] in memory for the fuller context. **Do not set `Ets2Enabled = true`
without live-verifying against a real ETS2 session first** - none of this has ever been checked
against an actual truck driving around, unlike every ATS fix in this feature, which all went through
real live testing before shipping.

## Toolchain

Same `truckermudgeon/maps` pipeline used for ATS (see the sibling `GENERATION.md`), pointed at the
ETS2 install instead. The five dev-time scripts (`build-route-graph.mjs`, `extract-map-features.ts`,
`extract-town-labels.ts`, `extract-prefab-curves.ts`, `extract-road-curves.ts`) were parameterized
to accept a map name (`usa` or `europe`) as an extra CLI arg, defaulting to `usa` so the existing
documented ATS commands are unaffected.

## Commands

```
cd _devtools/truckermudgeon-maps

NODE_OPTIONS="--max-old-space-size=24576" npx tsx packages/clis/parser/index.ts \
  -i "E:/Steam/steamapps/common/Euro Truck Simulator 2" -o out/parser

NODE_OPTIONS="--max-old-space-size=24576" npx tsx packages/clis/generator/index.ts map \
  -m europe -i out/parser -o out/tiles -t geojson

node --max-old-space-size=8192 tile-ats.mjs out/tiles/ets2.geojson out/tiles/pbf-europe

npx tsx extract-prefab-curves.ts out/parser out/route-graph-europe/prefab-connections.json europe
npx tsx extract-road-curves.ts out/parser out/route-graph-europe/road-curves.json europe
node build-route-graph.mjs out/parser out/route-graph-europe europe

npx tsx extract-map-features.ts out/parser out/map-features-europe europe
npx tsx extract-town-labels.ts out/parser out/map-features-europe/town-labels.json europe
```

Then `pbf-europe/**` zipped into `ets2-tiles.zip` (dropped in this folder), `route-graph-europe/`'s
three files zipped into `ets2-route-graph.zip` (dropped in `Assets/RouteGraph/`), and
`map-features-europe/*.json` copied into `Assets/WebMap/ets2-mapdata/`.

## Two real bugs found and fixed while doing this (not ETS2-specific)

- **`tile-ats.mjs` had the ATS map's bounding box hardcoded** (`{ west: -126, south: 23, east: -82,
  north: 50 }`), used to bound the tile-pyramid walk. Running it against `ets2.geojson` silently
  produced **zero tiles** - Europe's coordinates don't overlap the continental US at all, so every
  top-level tile in the walk came back empty. Fixed by computing the bbox from the geojson's own
  coordinates instead of a hardcoded region - works for whatever map it's pointed at now.
- **`build-route-graph.mjs` hardcoded the path to `prefab-connections.json`/`road-curves.json`** as
  `path.join(PARSER_DIR, '..', 'route-graph', ...)` - i.e. always `out/route-graph`, ignoring the
  `OUT_DIR` argument entirely. This never surfaced for ATS only because `out/route-graph` happened
  to *be* the default `OUT_DIR` too. Pointed at `out/route-graph-europe`, it silently read ATS's own
  curve files instead - all node/road uids failed to match, so the graph built with **zero** curve
  data (0 synthetic nodes from 0-matched road curves, 0 edges from the loaded prefab connections)
  and fell back entirely to straight-line chords through every interchange - the exact "cutting
  diagonal chords through interchanges" bug that was fixed for ATS, silently reintroduced. Fixed by
  reading both files from `OUT_DIR` instead of a hardcoded sibling path. Confirmed by rerunning:
  159,974 of 251,347 roads picked up real curvature (816,798 synthetic nodes), and all 322,644
  prefab curve connections resolved (2,311,234 edges, 0 skipped) - matches the shape of ATS's own
  numbers proportionally.

## Fixed since the 2026-09-01 build (pre-flight pass, not yet live-verified)

- **Orphaned/disconnected road fragments filtered out of `ets2.geojson`.** Ran
  `filter-orphaned-roads.mjs` (written for ATS, never applied to ETS2's own tiles until now)
  against the existing parser output: **42,095 of 143,124 road features (29.4%) were disconnected
  from the main drivable network** - decorative filler roads no player could ever actually reach,
  the same class of phantom the ATS pipeline found at 13.6%. Re-tiled after filtering: 137,267
  `.pbf` files (was 143,533), `ets2-tiles.zip` now 99.7MB zipped (was 111MB). The routing graph
  itself was never affected by this - it already pruned to its own single largest connected
  component independently - this was purely the *visual* tile layer catching up.
- **Company resolution: `MAX_SNAP_DISTANCE_METERS` raised from ATS's original 300m to 1,500m**
  (superseded by the ferry-connectivity fix below, which turned out to be the real fix for almost
  all of the remaining gap - see "Live-verified" for the full story). Diagnosed the distance
  distribution of the then-159 unresolved companies before picking a new number: only 8 sat within
  a plausible "just outside the main component" range (363-2,029m, the class of near-miss 300m was
  originally tuned for on ATS); the rest were 5km+ away, which turned out to mean "on a
  ferry-only-connected landmass" (see below), not "hopelessly disconnected." 1,500m safely captures
  the 8 fixable near-misses without reaching far enough to matter for the ferry cases either way.
  Re-running the same change against ATS's own graph moved it from 99.86% to a clean **100%
  (2,096/2,096)** - confirms it's a strict improvement, not an ETS2-specific tradeoff.
- **Frontend no longer hardcoded to ATS - `gpsmap.js` now reads which game is active and loads the
  matching tiles/mapdata folder.** Locally (WebView2), `GpsMapWindow`'s `Navigate()` call now
  appends `?game=ats` or `?game=ets2` (from `GpsMapViewModel.CurrentGame`, read fresh off
  `TelemetryService.LastSnapshot` so it can't lag behind); `gpsmap.js` reads that synchronously
  before any of the style.json/tiles/mapdata fetches happen, since a postMessage arriving after
  page load can't retroactively change what already got fetched. For LAN Mode, appending a query
  param to the URL a person has to type by hand into their phone's browser would just be one more
  thing to mistype - instead `GpsLanServer` gained a new `/api/game` endpoint (same pattern as its
  existing `/api/position`), and a remote page awaits that instead. Not live-tested against a
  running ETS2 session yet - this closes the "would just go quiet instead of switching maps" gap
  confirmed live on 2026-09-01, but confirming the *right* tiles/mapdata actually load still needs
  a real session with `Ets2Enabled` flipped on.
- **Car-only gas station filtering: confirmed not applicable to ETS2, not a bug.** Pulled all 70
  unique `gas_ico` prefab paths straight from `europe-pois.json` - none contain `_car_` or any
  other AI-only marker; ETS2's gas station prefabs are named by country/style
  (`gas_station_01_at.ppd`, `gas_plaza_01_fr.ppd`, etc.), not by player-vs-AI accessibility. Either
  ETS2 genuinely has no decorative AI-only gas stations the way ATS's background highway traffic
  does, or if it does, there's no data signal to distinguish them from real ones - either way,
  there's nothing left to filter on, and the fuel-icon layer showing every `gas_ico` POI is the
  correct behavior here, not a gap.
- **Batch connectivity test at scale, actually completed.** The original toy script
  (`test-route-batch-param.mjs`) timed out at this graph's size because it full-sorts its entire
  frontier array every single step - a real binary min-heap (matching the C# router's own
  `PriorityQueue`) instead: **199/199 random company-pair routes found a valid path**, averaging
  31.6ms/route. Ran the identical test against ATS's own graph as a baseline: 200/200, 4.7ms/route
  (faster only because it's the smaller graph). Both fully connected at scale, not just the one
  pair `test-route.mjs` happened to check originally.
- **Map's initial camera was still hardcoded to ATS's own center (`[-96, 39]`, the continental
  US).** Harmless in practice - it self-corrects the instant the first live position arrives and
  the view jumps to the truck - but it meant opening the GPS map for an ETS2 session started the
  camera over the Atlantic for a moment, generating a burst of tile-fetch 404s for a region with no
  ETS2 data at all (confirmed live, see below). Now picks a starting center based on the active
  game (`[15, 50]`, roughly central Europe, for ETS2).

## Live-verified 2026-09-06

Real ETS2 session, `Ets2Enabled` flipped on for the first time. Job: Rīga, Latvia to a company near
Saint Petersburg, Russia (571km, ~40 minutes real driving, crossing at least one international
border along the way).

- **Position landed correctly** - no reports of the truck appearing in the wrong place or the map
  showing a different region than expected.
- **Route computed once at job start and never recomputed for the entire trip** - zero "off route"
  deviation events the whole 571km/40 minutes, including through the border crossing. For
  comparison, an ATS job the same night (leaving a pickup yard) triggered ~30 recomputes in 11
  minutes just from yard maneuvering - this ETS2 route held perfectly the whole way, the strongest
  possible live signal that the graph and projection are both landing correctly for this route.
- **Non-Latin destination names (Cyrillic) render correctly end-to-end** - initially looked
  garbled when checked through a log-tail tool that guessed the wrong text encoding for a
  BOM-less UTF-8 file; re-read with explicit UTF-8 decoding (and confirmed against what actually
  showed on the HUD) proved the underlying data and on-screen text were both correct the whole
  time. Not a real bug - a false alarm from how the log was inspected, not from TruckHub itself.
- **Found and fixed live**: the ATS-hardcoded map camera center (see above) - caught from the
  tile-fetch error burst in the log, confirmed by converting the failing tile coordinates back to
  real lon/lat (they landed over the Atlantic/North America, not Europe).
- **Found and fixed live, much bigger one: the UK's entire road network was almost completely
  missing from the map.** Spotted directly from a screenshot - the UK showed city labels and
  nothing else, no roads at all, while the continent right next to it rendered normally. Root
  cause: the UK connects to mainland Europe *only* by ferry - no road or prefab link exists in the
  raw map data at all - and both `filter-orphaned-roads.mjs` and `build-route-graph.mjs`'s
  weak-connectivity pruning only ever looked at roads and prefabs. With no ferry data in the mix,
  the UK looked exactly like a giant orphaned fragment and got pruned right along with the genuine
  decorative junk: **92% of its roads (5,461 -> 426 features)** were being dropped from the tiles,
  and its companies were failing to resolve for the routing graph the same way.
  Fix: union each ferry terminal's own connections into the same connectivity check the roads and
  prefabs already build, so a real ferry-linked landmass counts as attached rather than orphaned.
  First attempt didn't work - a ferry terminal's own `nodeUid` turned out to be a standalone item
  node never referenced by any road or prefab at all (confirmed by checking Calais's own entry in
  `europe-ferries.json` against `europe-roads.json`/`europe-prefabs.json` directly); terminals
  connect to the real graph through their own small prefab instead, the same pattern a company's
  driveway already uses. Anchoring each terminal on its prefab's own boundary node instead fixed
  it properly: UK roads recovered to 3,477/5,461 (63.6%, up from 426), and **company resolution
  hit a clean 100% (1,844/1,844)** - every remaining ferry-isolated company (Sicily, Greek islands,
  Norway, Russia, and now the UK) resolves correctly. This is connectivity-for-pruning only, not a
  routable edge - a job route still correctly won't draw across a ferry crossing itself (that's a
  separate, still out-of-scope feature), but the destination now exists properly in the data
  either way, which it didn't before at all for anything UK-related.
  Re-ran the batch connectivity test after this fix: 167/200 (down from the earlier 199/200) - not
  a regression, the *opposite* of one. The 33 "failures" are all pairs where both companies are
  correctly connected to the graph now but sit on different ferry-only-separated landmasses from
  each other (e.g. Swansea, Wales to Helsingborg, Sweden) - a route between them genuinely
  shouldn't compute without a modeled ferry crossing, and previously those companies would have
  been silently absent from the graph entirely rather than correctly present-but-unroutable.
- **EXT window (gauges/axle status/wear) confirmed working live** - user-confirmed, same session.
- **LAN Mode's `/api/game` endpoint confirmed working live** - user-confirmed, same session.
- **Not yet checked this session**: the main dashboard's own ETS2-specific branches (round European
  speed sign, € currency, "engine brake" wording) - no code changes needed tonight (already-existing
  branches, shared telemetry), just hasn't been specifically eyeballed yet - low risk given EXT and
  LAN Mode (both bigger surfaces) already checked out fine.

## Deferred to a future version (not blocking v2.0)

- **No real country/state landmass shapes on the map, for either game.** Not a UK or ETS2-specific
  bug - checked both `ats.geojson` and `ets2.geojson`: every single `type: "country"` feature in
  both (California, France, the UK, all of them) is a bare `Point` (a label anchor), never a real
  polygon. `style.json`'s `state-fill`/`state-outline` layers have been dead code this whole time,
  filtering on a shape that was never produced. Far more visible on an island like the UK (floats
  with nothing under it against black water) than on a mainland country (blends into its
  neighbors, nobody notices the missing fill). Fixing this properly means sourcing real-world
  country border geometry from an external dataset (e.g. Natural Earth) and reprojecting it to
  match - the game's own data has never captured this at all, so there's nothing to extract from
  it. Explicit decision: leave this as a known gap for now, revisit for v2.1+.

## Still open

- **Highway shield extraction found zero matches** (2,982 road-type icons skipped, 0 signs
  extracted). `extract-map-features.ts`'s route-number regexes (`is\d+`/`us\d+`/`[a-z]{2}\d+`) are
  US highway-numbering conventions (Interstate/US Route/state route) - ETS2 uses European numbering
  (E-roads, per-country national/motorway prefixes) with a completely different icon-string format
  that hasn't been reverse-engineered yet. Highway shields simply won't render for ETS2 until this
  gets its own classification logic - cosmetic only, doesn't block routing. Not a live-testable
  item - this needs new classification logic, not a drive.
- **UK coordinate rescale** (`Ets2ProjectionService`) reimplements a documented-but-unofficial
  behavior (see that file's own comment) - not derivable from any SCS def file, and not live-tested
  against a real UK-region position at all. This one genuinely needs a drive through ETS2's UK DLC
  content specifically (a job to/from a UK city) to confirm the rescale lands positions correctly
  there - nothing else in this pass exercises that code path at all.
- A handful of repeated tile-fetch 404s during tonight's drive were for specific low-zoom cells
  actually inside the Baltic/Belarus/western-Russia region the route passed through (not the
  Atlantic hardcode above) - never showed up as a visible gap in the rendered road/route, most
  likely genuinely sparse map content at that zoom (no features in that cell means no tile file,
  which is correct, not a bug), but not conclusively ruled out as a real coverage hole. Worth
  watching for a repeat, not worth chasing blind.

## Result

251,347 roads / 665,462 raw nodes parsed; routing graph (post ferry-connectivity fix) pruned to
2,847,181 routable nodes / 3,732,173 directed edges (`nodes.bin` 21.7MB + `edges.bin` 56.9MB +
`companies.json` 123KB = 78.8MB uncompressed, 49.3MB zipped). Tiles (post orphaned-road-filter +
ferry-connectivity fix): 142,712 `.pbf` files, 104.3MB zipped - bigger than ATS's own numbers
across the board, consistent with ETS2's DLC map covering many more countries than ATS's US-only
extent.

## Re-running later

Same idea as ATS's own doc: re-run the full pipeline above with an up-to-date, fully-DLC'd ETS2
install, replace the three `ets2-*` output files in place - both `filter-orphaned-roads.mjs`
(`node filter-orphaned-roads.mjs out/parser out/tiles/ets2.geojson europe`, before `tile-ats.mjs`)
and `build-route-graph.mjs` now include the ferry-connectivity fix by default, no extra flag or
step needed - just make sure `${MAP}-ferries.json` exists in the parser output (it's part of the
standard parser run, nothing extra to generate). `WebMapAssetExtractor`'s cache-check hashes every
embedded resource's name+size, so a changed file is picked up automatically on next launch.
