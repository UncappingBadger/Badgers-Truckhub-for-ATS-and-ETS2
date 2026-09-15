# GPS map data - how it was generated

Generated 2026-08-30, against a legitimately-owned ATS install with all 19 released map DLCs
installed (`E:\Steam\steamapps\common\American Truck Simulator`): base + Nevada, Arizona, New
Mexico, Oregon, Washington, Utah, Idaho, Colorado, Wyoming, Texas, Kansas, Montana, Oklahoma,
Nebraska, Arkansas, Missouri, Iowa, Louisiana, Illinois. Full US coverage.

## Toolchain

`truckermudgeon/maps` (GPL-3.0-or-later), cloned to `F:\Claude Projects\_devtools\truckermudgeon-maps`
(outside this repo - the GPL-3 tool source never enters TruckHub's own git history, only its output
data does).

`tippecanoe` (needed for its own PMTiles output) has no Windows build and no WASM/JS port. Rather
than use WSL/Docker, the pipeline was split:

1. `truckermudgeon`'s own `parser` + `generator -t geojson` (both pure Node.js/TypeScript) produced
   `ats.geojson` - no tippecanoe involved at all for this step.
2. Two native addons inside `parser` (`cityhash`, `gdeflate` - used to read ATS's HashFS archive
   format) needed a real C++ compile. Built via Visual Studio Build Tools 2022 (C++ workload,
   installed just for this, removable afterward) + `git submodule update --init --recursive` (pulls
   in NVIDIA's `libdeflate` submodule the shallow clone misses).
3. `tile-ats.mjs` (a small custom script in the `_devtools` folder, not part of `truckermudgeon`'s
   own code) tiles `ats.geojson` into plain `{z}/{x}/{y}.pbf` vector tiles using pure-JS libraries -
   `geojson-vt` (ISC) + `vt-pbf` (MIT) - instead of tippecanoe. Zoom range 4-13, matching
   `truckermudgeon`'s own tippecanoe invocation (`-Z4 -z13`). MapLibre GL JS loads these directly as
   a plain XYZ vector source - no PMTiles container needed.

## Commands (approximate - see `_devtools/truckermudgeon-maps` for the actual scripts/state)

```
cd _devtools/truckermudgeon-maps
npm install --ignore-scripts
git submodule update --init --recursive
npx node-gyp rebuild --directory packages/clis/parser   # (run from packages/clis/parser)

NODE_OPTIONS="--max-old-space-size=24576" npx tsx packages/clis/parser/index.ts \
  -i "E:/Steam/steamapps/common/American Truck Simulator" -o out/parser

NODE_OPTIONS="--max-old-space-size=24576" npx tsx packages/clis/generator/index.ts map \
  -m usa -i out/parser -o out/tiles -t geojson

node filter-orphaned-roads.mjs out/parser out/tiles/ats.geojson usa

node --max-old-space-size=8192 tile-ats.mjs out/tiles/ats.geojson out/tiles/pbf
```

Then the resulting `out/tiles/pbf/**` was zipped into `tiles.zip` and dropped in here (this folder)
alongside the hand-written `index.html`/`gpsmap.js`/`style.json` and vendored `maplibre-gl`.

## Result

183,566 GeoJSON features -> (after orphaned-road filtering, see below) 167,417 -> 91,655 tiles,
~82MB zipped.

## Orphaned-road filtering (2026-09-01)

After a real drive, the user flagged a road on the GPS map that didn't actually exist in-game.
`truckermudgeon`'s own `generator map` command has zero connectivity filtering - it renders every
road that isn't `hidden`/`secret`, whether or not it actually connects to anything. Separately,
`build-route-graph.mjs` (see the sibling `Assets/RouteGraph/GENERATION.md`) already discovered the
game's own road/prefab data breaks into ~16,000 disconnected components when treated as a graph -
most of them tiny orphaned fragments (SCS's non-drivable decorative local-street fill inside city
blocks, disconnected from the actual drivable network) - and prunes the *routing* graph down to just
the single largest one. That pruning was never applied to the *visual* tiles, though, so exactly
this kind of disconnected fragment could still get drawn on the map as an ordinary-looking road with
no way to actually reach it - a very plausible match for "GPS shows a road that isn't really there."

`filter-orphaned-roads.mjs` (new script, same `_devtools` folder) reruns the identical weak
(undirected) union-find `build-route-graph.mjs` uses - roads and prefabs both link node uids
together - directly against `usa-roads.json`/`usa-prefabs.json`, then drops any `type: "road"`
feature from `ats.geojson` whose `startNodeUid`/`endNodeUid` aren't in the single largest component,
before tiling. Confirmed: 16,149 of 119,012 road features (13.6%) were orphaned fragments - a
sampled cluster near Davenport, IA (`us_tmpl01` local-road look, 56-node fragment, repeated many
times across different towns) matches exactly the kind of decorative, disconnected city-interior
filler road this was expected to catch. Run this step between the `generator map` command and
`tile-ats.mjs` any time the pipeline is re-run.

## Re-running later (e.g. after a new ATS map DLC releases)

Coverage is limited to whatever `.scs` files are physically present on the machine that runs
`parser` - there's no merge step for combining partial runs. Re-run the full pipeline above with an
up-to-date, fully-DLC'd install, then replace `tiles.zip` (`WebMapAssetExtractor`'s cache-check
hashes each embedded resource's name+size, so any change here is picked up automatically on next
launch - no manual cache-busting needed).

**Gotcha confirmed live doing this (2026-09-15, see below): `tile-ats.mjs`'s output directories are
additive, not a clean rebuild** - it only ever writes tiles that have features, so a stale `.pbf`
left over at a `{z}/{x}/{y}` that no longer has any feature in the new run never gets deleted. Always
`rm -rf` each `out/tiles/<target>` directory immediately before re-running `tile-ats.mjs` against it,
for the base roads and all three overlays alike - don't just re-run the command over an existing
output directory.

## Re-generated for ATS 1.61 (2026-09-15): Idaho speed-limits schema fix

ATS 1.61 shipped `def/country/idaho/speed_limits.sii` (or an equivalent generated def - not
hand-inspected, only the parsed result was) with `.speed_limit.truck`'s `maxLimit` array one entry
short of `laneSpeedClass`/`limit`/`urbanLimit` - `truckermudgeon`'s own `processSpeedLimitJson` in
`packages/clis/parser/game-files/def-parser.ts` asserted all four arrays equal length and crashed the
whole parser run on it. Patched (in the vendored GPL-3 `truckermudgeon-maps` clone, never shipped as
code - see the toolchain note at the top of this file) to pad any array shorter than
`laneSpeedClass` by repeating its own last element, generically (not Idaho- or freeway-specific), and
still assert equal length afterward as a sanity check. TruckHub's own consumer of this data
(`RoutingGraph`'s speed-class lookup) only ever reads `.limit`, never `.maxLimit`, so the padded value
has no effect on anything downstream. Confirmed live: Idaho's parsed `freeway.maxLimit` came out as
`70` (the padded repeat of `dividedRoad`'s value) against a real `freeway.limit` of `80` - exactly the
"array was one short" signature, on the exact field affected.

Full pipeline re-run end to end (ATS only - ETS2's `europe-*` parser output and `ets2-*` tiles/graph
were never touched, confirmed by timestamp before and after). Before -> after (pre-1.61 baseline,
generated 2026-08-30/31, vs. this run):

- Parser: `usa-companies.json` 2096 -> 2114 entries, `usa-roads.json` 218184 -> 219176, `usa-nodes.json`
  no longer directly comparable (parser's own node-writing changed between these runs; use the
  generator's own node-reference count instead, see below).
- Generator (`ats.geojson`): 183566 -> 184207 features.
- Orphaned-road filter: 184207 -> 168023 features (was 183566 -> 167417). 119167 roads total, 102983
  kept / 16184 dropped as orphaned fragments (13.6%, same ratio as the original 16149/119012 = 13.6%).
- Base tiling (`out/tiles/pbf`): 92311 tiles (was 91655), 79.0MB zipped.
- `extract-map-features.ts`: 1921 road-signs, 6202 poi-facilities (2067 car-only gas stations
  excluded), 2114 company-logos, 21 state-labels.
- Overlay tiling: poi-facilities 8637 tiles (1.7MB), road-signs 8970 tiles (1.6MB), company-logos
  8043 tiles (1.7MB).
- Route graph (see sibling `Assets/RouteGraph/GENERATION.md` for the full breakdown): 2195118 routable
  nodes / 2935795 directed edges (was 2187517 / 2926055), 2107/2114 companies resolved (99.67%, was
  2093/2096 = 99.86%).

`company-logos.zip` (the flat per-token PNG bundle) was deliberately left untouched - the logo artwork
itself didn't change, only marker positions/counts (covered by `company-tiles.zip` above).

## Map richness (2026-08-31): POIs, city labels, state borders, highway shields

Added directly on top of data already present in `ats.geojson`/the tiles - `truckermudgeon`'s own
generator already bakes POI positions+icons (`poiType`/`sprite`), city names+a population-derived
importance tier (`name`/`scaleRank`), and real ATS state-boundary polygons (`type: "country"`) into
the tile source, so most of this needed *no new data extraction at all*, just new `style.json`
layers styling what was already there:

- **State borders**: existing `state-outline` layer restyled for visibility (was nearly invisible -
  dark grey on a dark background, `line-width: 1`) - now dashed, brighter, zoom-scaled width.
- **City labels**: one `city-labels` symbol layer over every `type === "city"` feature (all 276),
  `minzoom: 4` (matching the source's own minzoom - i.e. no per-feature zoom gating at all).
  `icon-size`/color/dot-size scale continuously with `scaleRank` (drawn straight into each label's
  own canvas bitmap, not a MapLibre expression - see below), and `symbol-sort-key: scaleRank` makes
  MapLibre's built-in label-collision system prefer keeping major-city labels over crowded smaller
  ones when space is tight. An earlier version split this into four tiers (major/notable/town/small)
  each with a literal per-layer `minzoom` (4/6/8/10) specifically to gate small towns behind higher
  zoom - **this was wrong and got walked back**: live-compared against the in-game world map,
  Cheyenne (Wyoming's capital!), Chadron, Alliance, Scottsbluff, Sidney, Colby, Sterling, and
  Burlington were all genuinely missing at a zoom level where the actual in-game map shows them
  clearly - checking their `scaleRank` (3, 7, 8, 7, 10, 10, 10, 10) against the old tier thresholds
  confirmed the gating was simply too conservative, not that the data was missing anything. One
  layer, always-eligible-from-zoom-4, letting size/color + collision priority carry the hierarchy
  instead of hard zoom cutoffs, is both correct and much simpler.
- **Town labels** (`town-labels` source/layer, `minzoom: 5`, fixed `symbol-sort-key: 15` - always
  lower priority than any real city's 0-10): ATS's own 276-city list, it turns out, only covers
  larger/well-modeled cities - genuinely small settlements (highway-sign-only places with no full
  "city" entry) aren't in it at all, and the in-game world map's dense small-town coverage comes
  from a *different*, much larger dataset: `usa-mileageTargets.json` (1,327 entries - the same named
  locations shown on real in-game highway distance signs). Reuses truckermudgeon's own
  `LabelProducer` (`packages/clis/generator/geo-json/extra-labels.ts`, imported directly via `tsx`
  rather than reimplemented) to filter out unnamed junctions/state-line crossings/route-number
  targets and - critically - to skip any target `LabelProducer` already matched to a *real* city
  (`meta.show === false`), so a place with a proper `city-labels` entry doesn't also get a
  redundant, less-prominent `town-labels` one sitting on top of it. 622 of 1,327 raw targets survive
  that filtering. Re-run:
  ```
  npx tsx extract-town-labels.ts out/parser out/map-features/town-labels.json
  ```
  then copy the result into `Assets\WebMap\mapdata\town-labels.json`.
- **POI icons** (`poi-facility` layer, sourced from `mapdata/poi-facilities.json`) and **highway
  shields** (`road-signs` layer, `mapdata/road-signs.json`) are both produced by
  `extract-map-features.ts` (`_devtools/truckermudgeon-maps`, run via `tsx`), which writes both
  files in one pass:
  ```
  npx tsx extract-map-features.ts out/parser out/map-features
  ```
  then copy both results into `Assets\WebMap\mapdata\`.

  Originally POI icons were styled directly off the "ats" tile source's own `poiType`/`sprite`
  properties, needing no extraction at all - only highway shields did (their route-number
  classification, e.g. `"is80"` -> Interstate 80, `"us75"` -> US Route 75, `"ok3"` -> Oklahoma State
  Route 3, is real preprocessing worth doing once in plain JS/regex rather than fighting a much
  clumsier version of the same logic back together in MapLibre's expression language). **POIs
  needed their own extraction too, once it turned out `gas_ico` was showing fuel icons with nothing
  at that location in-game**: `usa-pois.json`'s `prefabPath` field distinguishes a real,
  truck-usable gas station from an AI-traffic-only "car" one (e.g.
  `"us_gas_chemron_car_4rows_roof_trigger.ppd"` vs `"...truck_5rows_roof.ppd"`) - 2,072 of 4,381
  `gas_ico` POIs turned out to be `_car_` prefabs, decoration for AI traffic that a player's truck
  can't actually pull into. That field isn't preserved into the tiles at all (truckermudgeon's own
  generator drops it, and the tile-embedded `sprite`/`poiType` properties have no equivalent
  signal), so it has to be filtered here, working from the raw parser data, not stylable from the
  tile source. Categories match what was actually asked for: fuel (`gas_ico`, car-only excluded),
  weigh stations (`weigh_station_ico`), rest stops (`parking_ico`), and service/repair
  (`service_ico` + `garage_large_ico`, merged - ATS distinguishes them but the ask didn't).
  Dealer/recruitment-office icons exist in the same data but were left out to avoid clutter beyond
  what was requested.

  Shield *shapes* were originally just generic same-shape rounded badges differing only by border
  color - the user's own reference (a real MUTCD "Route Signs" figure) made clear that read as
  nothing like real highway signage. Redrawn in `gpsmap.js`'s `drawShield()` with the actual MUTCD
  M1-1/M1-4/M1-5 silhouettes: Interstate is a proper pointed shield (navy fill, red band, white
  border/number), US Route is a notched-top/rounded-bottom cutout shield (white/black), state route
  is a plain circle (white/black) - the common generic default most states without their own bespoke
  marker shape use. These are real US federal/state highway-standard designs - government works with
  no copyright - so replicating the actual shapes/colors directly, rather than a licensing-cautious
  generic stand-in, is fine.

- **Company markers** (`company-logo` layer, tiled the same way as poi-facilities/road-signs above -
  2096 ATS / 1844 ETS2 points, the same order of magnitude that forced those two into real tiles).
  `extract-map-features.ts` writes `company-logos.json` in the same pass as the other two, straight
  off `usa/europe-pois.json`'s own `type: "company"` records - no separate lookup/join needed, since
  each record already carries position, a real display `name` (a literal string from that company's
  own `def/company/<token>/company.sii` - checked every usa/europe-companyDefs.json entry, zero
  `@@locale_key@@` references, so no locale-file resolution step exists to get wrong), and `icon`
  (the company token, doubling as its logo's filename).

  Logo *images* are a genuinely different kind of asset from everything else on this map: real,
  finished artwork SCS designed for their game, not geometry/symbology this project derived or drew
  itself (contrast the MUTCD shields above, which are US federal/state highway-standard designs -
  government works, no copyright issue replicating them exactly). Extracted from
  `material/ui/company/small/<token>.tobj` inside the game's own archives, via `truckermudgeon`'s own
  already-working `.tobj`→DDS→PNG decode pipeline (`packages/clis/parser/game-files/scs-archive.ts` +
  `dds-parser.ts`, pure JS, no native step) - this had already been run as a side effect of other
  parser work, so no new extraction tooling was needed, just discovering the PNGs already sat at
  `out/parser/icons/<token>.png`. 498 unique company tokens across both games combined (zero overlap
  between them - confirmed), each logo 128x32px, ~1.9MB total - small enough to ship as one shared
  `company-logos.zip` (flat `<token>.png` files, not tiled - see `WebMapAssetExtractor.cs`) rather
  than needing per-tile embedding. `gpsmap.js`'s `styleimagemissing` handler fetches the matching PNG
  by token the first time a `company-logo:<token>` id is actually requested, the same lazy-load
  pattern city/state labels and highway shields already use, just backed by a real image instead of a
  canvas drawing. Re-run alongside the other two (`extract-map-features.ts`), then
  `node tile-ats.mjs out/map-features/company-logos.json out/tiles/pbf-company company-logos 7`
  (and the ETS2 equivalent) to retile.

**No MapLibre `text-field` anywhere** - this style never configured a `glyphs` URL (an earlier
attempt at `"glyphs": null` was invalid and removed outright, so there was never any font-glyph
pipeline here), which `text-field` requires. City/town names and shield numbers are instead drawn
as plain 2D canvas bitmaps in `gpsmap.js` and registered via `map.addImage()` - facility icons
(fuel/weigh/rest/service - a small fixed set) are drawn once on map load; city-label/town-label/
road-sign bitmaps are drawn lazily via the `styleimagemissing` event the first time each unique one
is actually requested (id format `city-label:<name>:<scaleRank>` / `town-label:<name>` /
`road-sign:<shieldType>:<number>`), since there's no way to enumerate every possible name/route
number up front - MapLibre caches by id afterward, so each unique label is only ever drawn once no
matter how many features share it.
