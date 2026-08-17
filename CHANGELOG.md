# Changelog

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
