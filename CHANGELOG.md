# Changelog

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
