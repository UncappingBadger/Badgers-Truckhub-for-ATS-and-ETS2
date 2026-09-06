# Changelog

## v2.0.0 - 2026-09-06

### Added
- **Euro Truck Simulator 2 support** - TruckHub now works with ETS2 as a first-class second game, not just ATS. Live GPS map, routing, and every EXT/dashboard feature below all work the same way regardless of which game is running. Includes a real road-following map covering the full ETS2 map (all owned DLC regions), with the UK correctly included as a real, drivable part of the network even though it's only reachable in-game by ferry.
- **Extended Instruments (EXT) window** - a second instrument panel (its own tab on the main card) for everything the main dashboard doesn't have room for: analog gauges for oil pressure, oil temperature, water temperature, battery voltage, brake air pressure, and brake temperature (color-coded green/amber/red using real heavy-truck service thresholds, and the battery gauge automatically scales to your truck's actual electrical system - 12V for ATS, 24V for ETS2 - instead of assuming one fixed voltage); a segmented suspension-deflection readout per wheel for both truck and trailer, with liftable axles clearly labeled LIFT or TAG and showing UP in amber when actually raised; trailer axle status (DOWN/LIFTED/AIRBORNE per wheel); and accessory wear for truck and trailer. Every element can be switched on or off individually from its own settings window, same as the main dashboard.
- **90's-style mechanical odometer** - a six-digit ticker under the speedometer, showing lifetime whole-mile/km travel with a tick-flash on every digit rollover. Toggleable in Settings, and switches units live with the existing KM/MPH setting.
- **GPS map facility markers redrawn as real pictograms** - fuel, service, and rest-stop markers on the map are now a pump, a wrench, and a bed icon respectively, instead of plain letter badges.
- **Live GPS map** - a real, road-following map showing your truck's live position over actual game roads, cities, and state/country boundaries, with a route line to your active job's destination (or a manually-dropped pin) that automatically reroutes if you drift off-path. Includes real highway shields (ATS), road color-coding (interstate vs. state route vs. local), state/country borders/labels, and city/town labels all the way down to the small towns you pass through.
- **LAN Mode** - toggle it from the GPS window to view your live position and route from any browser on your local network (phone, tablet, second PC). Keeps running even if you close the main GPS window.
- **GPS window controls** - minimize, maximize, reset-to-default-size, and fullscreen.
- **Gearbox calibration hotkey (Ctrl+Alt+G)** - recapture your shifter's gear positions on the fly without reopening Settings, for both standard and splitter/range shifters.
- **GPS / LOG / EXT launcher tabs** - the small map-icon button is gone; opening the GPS map, job log, or extended instruments is now a set of docked tabs on the main window.

### Changed
- Substantial performance work for the GPS map: CPU-aware core management now also covers the map's own browser subprocesses (not just TruckHub itself), a dynamic 30/60fps cap responds to real system load, and the map's browser engine now pre-warms quietly in the background at app startup instead of costing you a slow first open.

### Fixed
- Fuel station markers no longer appear at AI-traffic-only locations that don't have a real, truck-usable station in-game.
- A handful of non-canonical "secret" construction roads no longer render as if they were real, driveable roads.
- Truck and trailer axle/suspension readouts no longer pad out to a generic maximum wheel count - only the axles your specific vehicle actually has are shown.

## v1.2 - 2026-08-17

### Added
- **Suggested mod load order** - the ☰ button scans your ATS/ETS2 mod folder (on demand, per game, never automatically) and suggests a load order based on the community-recommended category order, plus a new Map Mods tier that loads first. Reads each mod's manifest and, where that's not specific enough, keyword-matches its name (lights/LED packs, tuning packs, transmissions, map-expansion mods all recognized); anything it still can't confidently place shows up as "Unsorted" rather than a guess. A mod and its "Open Def" companion are always kept together and classified the same way, never split apart. Numbered continuously so the order reads unambiguously. A manual mod-folder override was added to Settings alongside the existing game-install override, for anyone with a nonstandard setup.
- A clear disclaimer that the suggested order is the community's best effort, not a guarantee - some mods can still conflict regardless of order.

### Fixed
- The mod-order scan's "folder not found" / "no .scs files" messages weren't actually visible - a string property was bound directly through `BooleanToVisibilityConverter`, which silently collapses anything that isn't a real bool, so a genuinely-empty scan looked like nothing had happened.

## v1.1.1 - 2026-08-16

### Changed
- TruckHub now reserves a slice of CPU cores it will never use (2 cores on 4+-core machines, 1 on 2-3 core machines), guaranteeing ATS/ETS2 always has uncontested room regardless of what TruckHub's own UI is doing. Matches the same protection already used in this developer's other companion apps (PalHub, ZoidHub).

## v1.1 - 2026-08-02

### Added
- **Job logbook** - a running record of every job delivered this session (pickup, drop-off, mileage, payout, running total). Lives in a dockable slide-out drawer, or can be popped out into its own independently resizable window.
- **Redesigned Fuel & AdBlue gauge** - a real analogue-style dial for fuel with a needle that sweeps to the actual level, plus a 4-segment level bar for AdBlue built into the same gauge face.
- **Redesigned Speed & RPM gauges** - realistic dial treatment (tick marks, numbered scale, eased needle sweep, RPM redline zone) matching the fuel gauge.
- **Automatic-transmission support** - the gear display automatically switches to D/N/P/R for automatic/torque-converter trucks instead of a numbered gear.
- **Rest timer** *(optional)* - time left before a break is needed, for players who drive with the fatigue simulation enabled (toggle in Settings).
- **Retarder & engine-brake symbology** - dashboard icons showing the actual engaged retarder level and engine-brake state, grouped with the parking brake since they're the same braking system.
- **In-game clock** - a digital clock display under the speed limit sign, always visible regardless of an active job.
- **Auto-updater** - checks GitHub for a newer release on launch (fails silently if offline) and via a manual "Check for Updates" button in Settings; the ⚙ icon glows amber when an update is available.
- **Redesigned Settings window** - borderless, with browser-style tabs splitting "App Configure" (updates, game install, gearbox calibration) from "UI Setup" (units, display toggles, fatigue).

### Fixed
- Gear range indicator (H/L) now only updates when the truck actually completes a gear change, instead of reflecting the H-shifter's Low/High switch position live - previously it could show "H" while the truck was still mechanically in Low.
- Job log drawer no longer inflates the main window's height while closed.
- Warning-cluster lights (fuel, AdBlue, air pressure, etc.) now correctly go dark when the truck's electrics are off, matching the fuel/AdBlue needle.
- Pop-out job log window no longer opens off-screen when the main window is docked near a monitor edge.
