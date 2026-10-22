# Changelog

## v2.5.0 - 2026-10-22

### Added
- ALIGN window: help with backing a truck onto a trailer. It is a reversing-camera-style view - colour-graded guide
  lines with cross ticks, a dashed centre line, the trailer's own centreline and a target ring on its kingpin - that
  shows how far the trailer's kingpin is from your fifth wheel sideways, back and in height, and turns green when you are
  lined up (within about 5 cm sideways and 5 cm in height, the tightest the games have been seen to accept). Four
  readouts underneath (side, gap, height, trailer angle) follow your unit setting. The trailer itself is drawn as a 3D
  model standing where the game has it, in the game's own trailer type (dry van, curtain-sider, refrigerated, flatbed,
  lowboy, container, tanker, silo and so on) and with its real axle layout; a trailer type from a mod that TruckHub
  does not know is drawn as a generic box trailer. It fades as it gets close so the road and the target stay visible.
  It works for a trailer you have dropped
  and for a job's trailer waiting in the yard (the moment you accept the job), in either coupling mode, in both games. If
  the trailer is beside or ahead of you it points toward it instead.
- The ALIGN tab sits with GPS, LOG and EXT on the main window's right edge but opens ABOVE the main window, and like the
  job log it can be popped out into its own resizable window. It also opens and closes from inside the game with the
  hotkey Ctrl+Alt+. (period) - a key combination neither game uses. Settings > App Configure > ALIGN has a switch to turn
  the hotkey off if another program on your PC already uses it. Like the main window and EXT, it shows only "Waiting for
  ETS2 / ATS to start..." until a game is connected; the picture and readouts appear once it is.
  The two guide lines follow your steering like a pickup's reversing camera: they bend to show where the fifth wheel
  will go if you reverse with the wheel as it is now, so you can steer the kingpin between them. Nothing needs setting
  up: TruckHub learns each truck's turning from a few seconds of driving - mods and every chassis included - and keeps
  refining it, more accurately each time you drive that truck (what it has learned is kept per truck in
  hitch-steering.json next to settings.json). The lines stay straight back until it has learned the truck, while driving
  forward, and once hooked up.
  It also warns you to slow down: reversing faster than 2 mph (3.2 km/h) with the trailer within 5 m turns the top banner
  red ("SLOW DOWN" and your speed, in mph or km/h to match your settings) and pulses a red frame around the picture. The
  game's collision damage grows with the square of the impact speed and has no safe minimum, so this is a baseline to
  stay well under rather than a guarantee.
  ALIGN can beep like a car's parking sensor (Settings > App Configure > ALIGN: its own on/off switch and volume, 50 %
  by default, with a Test button). Slow beeps start about 5 m from the kingpin and get faster as you close in, then it is
  one steady tone when you are at it. A higher pitch means you are lined up and a lower one that you are off line, and the
  sound moves toward the side the kingpin is on. Backing in over 2 mph near the trailer gives a distinct two-tone warning,
  and going past the hook gives rapid low beeps. They only sound while the truck is in a reverse gear, go quiet when you
  stop, and only sound while the ALIGN window is open.

### Changed
- The bundled telemetry plugin (SCS Telemetry Hub) is updated to 0.1.3: it now also reports where the fifth wheel and
  kingpin are, each trailer's body type and wheel layout, and the steering angle of the wheels - what ALIGN is built on.
- The plugin installer never downgrades the plugin. The plugin files carry no version number, but each one carries the time
  it was built, so if the plugin already in a game's folder was built after the one bundled with the TruckHub you have
  just started (a newer TruckHub's, or a test build placed there by hand), it is left alone and the log says so. Before,
  any plugin that differed from the bundled one was overwritten, which let an older TruckHub replace a newer plugin.

## v2.4.0 - 2026-10-18

### Added
- Speed limit change beep: a single short beep whenever the posted speed limit changes - a higher
  tone when it goes up, a lower tone when it goes down. It never sounds for a unit switch or when
  limit data first appears after loading, and at most once every 3 seconds when limits change in
  quick succession.
- Over-limit warning sound: a short, quiet double beep the moment your speed turns red (more than 5
  over the limit - the same point the speed readout changes from amber to red). It sounds once per
  crossing, not continuously, and never on connect or when unpausing while already over. If the
  limit drops under you, you hear the warning rather than both sounds.
- Both sounds have their own on/off switch, volume slider (0-100%, default 50%) and Test button under
  Settings > UI Setup > Speed alerts. They are independent, so the frequent limit beep can be turned
  down without touching the warning.
- Rendering options (Settings > App Configure > Rendering): two independent switches choose whether
  TruckHub's HUD windows and its GPS map are drawn by the graphics card (GPU, the default) or the
  processor (CPU). Useful when a game is already maxing out the GPU and TruckHub's HUD stutters, freezes
  or shows black boxes, or the GPS map competes with the game. Each takes effect on the next start, and
  Settings offers a Restart now button.
- The GPS map's next-turn sign now works in Euro Truck Simulator 2 as well as American Truck Simulator. It is the
  same sign in European blue (the colour of that map's national-road plates), shows the turn direction and
  distance the same way, and follows your speed unit (miles and feet, or kilometres and metres).

### Changed
- The GPS map no longer rebuilds your route by itself when you leave it. Stopping for fuel or a rest area -
  including down a side road - used to trigger a full recalculation as soon as you were 200 m (about 650 ft) off
  the route. Now nothing happens until you are a mile or more from it; then a red OFF ROUTE warning appears at the
  top of the map together with a **Reroute** button, and you choose when to recompute (the button, or Ctrl+Alt+F8
  from inside the game). The warning clears once you are back on the route, and the next-turn arrow is hidden
  while you are off it so it cannot point you wrong. These are miles of actual driving (about a minute at highway
  speed): the games squeeze their maps and run the clock fast, so the odometer counts several miles for each one -
  about 3 on slow roads and in towns, 15 on UK motorways, a steady 16 in a multiplayer convoy. Settings > App
  Configure > GPS route has "Recompute the route automatically when I leave it" to restore the old behaviour
  (recalculate beyond 200 m); it is off by default.
- TruckHub now installs only its own SCS Telemetry Hub plugin into your game folders, and the old RenCloud
  telemetry plugin and the code that read it have been removed from TruckHub completely - nothing used them, and
  every extra plugin is one more module that can fault when a game exits. Copies already in your plugins folder
  are left exactly as they are.
- The GPS route finder now uses about a fifth of the memory. Its road network is stored in a compact
  layout (roughly 50 MB for the American map, 62 MB for the European, down from 226 MB and 284 MB), which
  should take around 175-220 MB off TruckHub's memory while the GPS map is open. Route search is a little faster
  as a side effect, and the routes themselves are unchanged (verified identical on 600 random routes across both
  maps).

### Fixed
- The next-turn sign's distance read a small fraction of the real figure (a turn the in-game GPS called "1 mile"
  showed as roughly 270 ft). The sign was counting miles of actual driving, while the odometer and the in-game GPS
  count several miles for each - and how many changes with the road (about 3 in towns, 15 on UK motorways, a steady
  16 in a multiplayer convoy). It now measures that ratio from your own odometer and movement as you drive and scales
  the distance to match the in-game GPS; replayed over a real ETS2 drive it was within about 5% for the next
  few hundred metres, where a fixed factor was out by a factor of five or more. A turn beyond a change of road
  type can still be a little off.
- If the telemetry hub's live stream goes quiet while the game is still running (seen after a game
  reload resets the game's clock), TruckHub now keeps up by reading the hub's snapshot directly
  instead of sitting on the last frame it received - which could leave the gauges showing the
  engine idling long after it was switched off.
- The GPS map's files are no longer unpacked again (about five minutes of heavy CPU and memory use) after every
  update or UI change - only when the map's own content actually changes.
- The Distance card no longer forgets the job's total length when you close TruckHub, or quit and reload the game,
  part-way through a job. It used to take the distance *remaining* at that moment as the new total (a 1,100-mile
  job reloaded 600 miles from the end read "of 597 mi" and the miles already driven vanished). The total is now
  saved per job and restored on the next launch; it is cleared when the job is delivered or cancelled. For a job
  TruckHub first meets part-way along (nothing saved), the total starts from the job's quoted length instead.
- The EXT window's battery gauge needle sat on its minimum permanently in American Truck Simulator (the readout
  was right, the needle was not): it was scaled for a 24 V truck, and the telemetry hub doesn't report the
  truck's battery warning level for it to scale from. It now uses a 12 V scale for ATS.
- The EXT window no longer keeps showing a trailer that has left your truck. When the game takes the trailer away
  from the truck (for example through a menu), the telemetry hub used to keep reporting the old trailer as attached
  with its last readings, so the trailer section stayed filled in. It now reports the trailer as detached, and the
  section reads "No trailer attached" until the next one is hooked up.

## v2.3.1 - 2026-10-11

### Fixed
- The time-to-delivery readout on the main card never counted down when TruckHub was using the
  SCS Telemetry Hub (v2.3.0's default source): it showed one fixed number for the whole job,
  unaffected by driving or resting. It now shows the real time left - falling as the game clock
  advances, jumping when you rest, and reading "Late ..." once the deadline has passed - the same
  as it did with the previous plugin. Affects v2.3.0 only.
- TruckHub no longer overwrites a custom build of `scs-telemetry.dll` that another tool installed.
  If a manifest (a `.json` naming that file) sits beside it in the plugins folder, TruckHub now
  leaves the file alone instead of replacing it with its bundled copy on every launch. A note is
  written to TruckHub's log when that happens. Nothing changes for installs without such a manifest.

### Changed
- Bundled SCS Telemetry Hub plugin updated. It now shuts down properly when the game closes (open
  streams end cleanly and its network port is released immediately), and stays loaded until the
  game process exits, which removes a rare access violation that was logged at game exit. It also
  no longer writes over a hundred "wheel index not valid" error lines to the game log on every
  start, and writes a clear warning to the game log if its port is already in use.

## v2.3.0 - 2026-09-30

### Added
- New primary telemetry source: TruckHub's own SCS Telemetry Hub plugin, replacing the
  unmaintained RenCloud plugin (no updates since September 2023) as the active data source.
  Unlocks job info and Trip Needs & Economy for ATS's Road Trip DLC "Quick Job" personal-car
  courier jobs, which the old plugin has no way to ever support. Dashboard telemetry, GPS
  routing, and refuel detection all carry over with full parity, on both truck and car mode, in
  both games. The old plugin stays installed as a dormant fallback.

### Fixed
- The installer only ever deployed the old RenCloud plugin - the new one was never actually
  copied into either game's plugins folder for anyone except this machine's own dev setup. Fixed
  before this reached anyone: both plugins now install side by side automatically, same as
  always, no extra steps.
- A manually-set game install path that pointed at a folder that existed but wasn't actually a
  game install (e.g. a stale path left over from earlier troubleshooting) silently overrode a
  perfectly good auto-detected install, including for plugin installation - found while testing
  the fix above. Manual paths are now validated the same way the Settings screen already warns
  about, and fall back to auto-detection when they don't look right.
- Hardened against a rare but real risk: the telemetry client's shutdown path now always
  releases its shared-memory handle, even on an interrupted startup; the new plugin's local HTTP
  server now caps concurrent connections instead of spawning one thread per connection
  unbounded. Neither was linked to any specific bug, but both closed real gaps found during a
  crash-safety audit prompted by unrelated game crashes (traced to third-party mods, not
  TruckHub - see below).

### Maintenance
- Investigated a recurring `amtrucks.exe` hard crash reported during a convoy session. Windows
  Event Log forensics and two rounds of code audit (this plugin and the C# app) found nothing in
  TruckHub's own code that could cause it - the crash signature also reproduced in singleplayer,
  pointing at a multi-plugin interaction among other, unrelated third-party mods instead. Root
  cause outside TruckHub's control; the hardening above shipped anyway as cheap insurance.

## v2.2.1 - 2026-09-26

### Maintenance
- GPS map/route data refreshed for ATS's South Dakota map-expansion DLC - new roads, cities
  (Sioux Falls, Rapid City, Pierre, and more), companies, and highway signage are now covered.

## v2.2.0 - 2026-09-15

### Added
- ETS2 GPS map now shows real European highway shields - green for E-roads/motorways/expressways,
  blue for national and regional roads - matching the same "replicate the real sign" approach as
  the existing ATS Interstate/US Route shields. Also corrected those existing Interstate shield
  colors, which were a noticeably duller approximation of the real AASHTO/FHWA navy-and-red.
- GPS map (ATS only for now) shows a next-turn sign in the bottom-right corner - a green
  advance-guide-sign-style plate with a directional arrow and distance, toggleable from its own
  button in the map's toolbar (on by default). ETS2 support is a planned follow-up once this is
  confirmed working on real driving.
- GPS map now shows real delivery-company names and logos at each marker (both games), sourced
  from the games' own data - see `THIRD_PARTY_NOTICES.md` for what's shipped and why.

### Changed
- The player-position marker on the GPS map is 25% larger - it was blending into some road
  colors at normal zoom levels.

### Fixed
- GPS map dragging/panning felt clunky, especially with facility icons and highway shields
  visible - both overlays were being held and re-evaluated as one flat dataset regardless of
  what was actually on screen. Converted to real vector tiles, same as the base road layer, so
  only what's currently visible is ever touched.
- Zooming back out after zooming in caused a stutter burst while the map refetched everything
  it had evicted from cache. MapLibre's tile cache had no explicit size set, so it defaulted to
  roughly the current viewport's own tile count.

### Maintenance
- Map/route data refreshed against ATS 1.61 (new gas stations, map polish in CA/OR/WA/ID/MT/WY).
  Also fixed a real crash in the underlying map-data tooling caused by a schema inconsistency in
  that patch's Idaho speed-limit data.

## v2.1.0 - 2026-09-06

### Added
- ETS2 GPS map now routes straight across ferry and train crossings (the UK to mainland Europe,
  most notably) as part of the same job, instead of stopping at the water's edge. Each crossing is
  weighted by its own real in-game time, so it's treated like any other stretch of the route rather
  than a rough guess.

## v2.0.1 - 2026-09-06

### Fixed
- ETS2 GPS map: after a job that crosses the Channel (UK to mainland or back), the route now picks
  back up automatically once you're actually off the ferry/train, instead of staying blank for the
  rest of the job. Found and confirmed live the same day, on a real Newcastle-upon-Tyne -> Dijon
  run.

## v2.0.0 - 2026-09-06

The big one. ETS2 support, a whole new instruments panel, and a bunch of smaller stuff that's been on the list for a while.

### Added
- **ETS2 support** - TruckHub isn't ATS-only anymore. GPS map, routing, EXT, all of it works the same whether you're hauling in America or Europe. UK's on the map too, even though the only way in is by ferry.
- **Extended Instruments (EXT) window** - a second panel for everything that didn't fit on the main dashboard: oil pressure and temp, water temp, battery voltage, brake air pressure and temp, suspension deflection per wheel on truck and trailer, lift/tag axle status, accessory wear. Battery gauge knows the difference between a 12V ATS truck and a 24V ETS2 truck. Everything on it can be turned on or off from its own settings window.
- **A proper mechanical odometer** - six digits under the speedometer, ticks over like the real thing, little flash when a digit rolls. Follows your KM/MPH setting.
- **Better-looking map markers** - fuel stops, service, and rest areas now show as an actual pump/wrench/bed instead of letter badges.
- **Live GPS map** - your position over real roads, with a route to your job that reroutes if you go off track. ATS gets real highway shields; ETS2 doesn't have those yet (see below).
- **LAN Mode** - watch your live position and route from a phone or tablet on the same network.
- Minimize/maximize/reset/fullscreen on the GPS window.
- **Ctrl+Alt+G** to recalibrate your shifter without digging into Settings.
- GPS / LOG / EXT are tabs on the main window now instead of one map-icon button.

### Changed
- CoreBalancer now looks after the GPS map's browser process too, not just TruckHub itself, and drops the map to 30fps when things get busy, back to 60fps once they don't. The map's browser also warms up quietly at startup so the first time you open it isn't slow.

### Fixed
- Fuel markers stopped showing up at places that are AI-traffic only.
- A few roads that were never meant to be driven don't render as real roads anymore.
- Axle/suspension readouts only show the wheels your truck actually has instead of padding out to a generic max.

### Known gap
- Highway shields don't render for ETS2 yet - the road-numbering there is a different format and hasn't been mapped to shield art. Cosmetic only, doesn't affect routing.

## v1.2 - 2026-08-17

### Added
- **Suggested mod load order** - hit the ☰ button and it scans your mod folder and suggests an order, based on the usual community category order plus a Map Mods tier that goes first. Reads manifests where it can, falls back to keyword matching (lights, tuning, transmissions, map mods) where it can't, and calls anything it's not sure about "Unsorted" rather than guess. A mod and its "Open Def" companion always stay together. Added a manual mod-folder override in Settings for anyone with a nonstandard setup.
- A disclaimer that this is a best-effort suggestion, not a guarantee - mods can still conflict regardless of order.

### Fixed
- The "folder not found" / "no .scs files" messages from the mod scan weren't actually showing up - turned out a string was bound through a converter that only handles real booleans, so an empty scan just looked like nothing happened.

## v1.1.1 - 2026-08-16

### Changed
- TruckHub now leaves a couple of CPU cores alone entirely (2 on a 4+-core machine, 1 on 2-3 cores), so ATS/ETS2 always has room no matter what TruckHub's UI is doing. Same approach used in PalHub and ZoidHub.

## v1.1 - 2026-08-02

### Added
- **Job logbook** - a running record of what you've delivered this session: pickup, drop-off, mileage, payout, running total. Lives in a slide-out drawer or its own resizable window.
- **Redesigned fuel & AdBlue gauge** - real analogue dial with a needle, plus a 4-segment AdBlue bar on the same face.
- **Redesigned speed & RPM gauges** - tick marks, numbered scale, eased needle movement, redline zone.
- Automatic-transmission trucks show D/N/P/R instead of a gear number.
- **Rest timer** *(optional)* - time left before a break, for anyone driving with fatigue simulation on.
- Retarder and engine-brake icons showing the actual engaged level/state, next to the parking brake since it's the same system.
- **In-game clock** under the speed limit sign, always visible.
- **Auto-updater** - checks GitHub on launch, or manually from Settings. The ⚙ glows amber when there's something new.
- Settings window redone - borderless, split into "App Configure" and "UI Setup" tabs.

### Fixed
- Gear range indicator (H/L) now only changes when the truck actually finishes shifting, not the instant you flip the H-shifter switch.
- Job log drawer no longer stretches the main window while closed.
- Warning lights (fuel, AdBlue, air pressure) go dark with the electrics off like they should.
- Pop-out job log no longer opens off-screen when the main window's docked near a monitor edge.
