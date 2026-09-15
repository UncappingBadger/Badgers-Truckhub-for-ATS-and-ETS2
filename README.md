# TruckHub

A small always-on-top overlay for Euro Truck Simulator 2 / American Truck Simulator that shows the
information you actually want while driving, without alt-tabbing or squinting at the in-cab dash.
Works the same whether you're hauling in the US or across Europe, UK included.

*TruckHub is an independent fan-made project, not affiliated with or endorsed by SCS Software.
It mainly reads live telemetry the games publish through their own official SDK; the GPS map's
road/route data is derived from the games' own files, and its company markers embed a small set
of the games' own logo images (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for exactly
what and why).*

## What it shows

- **Speed sign** — the speed limit, shown as a US-style rectangle in ATS or a round European sign in
  ETS2, matching whichever game you're actually in
- **Turn signals** — green arrows either side of the speed sign, flashing in sync with your actual
  indicators; both flash together automatically when hazards are on
- **Lights cluster** — a row of icons, braking system first (parking brake, retarder — showing the
  actual engaged level, not just on/off — and engine/Jake brake, grouped together since they're all
  the same system), then sidelights, low beam, high beam, beacon, differential lock, and lift/tag
  axle position (full breakdown in the manual — see below)
- **In-game clock** — a small digital-clock display directly under the speed limit sign, in green
  monospace digits so it's never mistaken for your PC's actual clock; always visible regardless of
  whether you've got an active job
- **Warning lights** — the same low air pressure / emergency brake / oil pressure / engine temp / low
  fuel / low AdBlue / battery warnings your dashboard has, dimmed until something actually needs
  attention
- **Current speed** — its own gauge with a ring that fills as you speed up, coloured green while
  you're at or under the limit, amber up to 5 over, red beyond that
- **Odometer** — a six-digit mechanical-style ticker under the speedometer, tracking lifetime
  distance and flashing on every rollover, just like the real thing
- **Cruise control** — lit up when it's on, showing the speed it's set to
- **Gear & RPM** — gear in its own box, showing the actual gear number for manual/H-shifter trucks
  (see H-shifter calibration below) or **D / N / P / R** automatically if you're driving an automatic;
  RPM as a gauge that fills toward the engine's actual redline and changes colour as it gets close
- **Job info** — route, cargo and weight, payout, distance travelled, time left
- **Fuel & AdBlue gauge** — a real analogue-style dial for fuel with a needle that sweeps to the actual
  level (not a snap-to-value jump), plus a 4-segment level bar for AdBlue built into the same gauge
  face on trucks that have one
- **Trip Needs & Economy** — what percentage of your fuel tank the rest of this trip will use (green if
  you've got enough, red if you don't), plus your actual MPG or L/100km based on real driving, not a
  spec-sheet number
- **Rest timer** *(optional)* — time left before you need a break, for players who drive with the
  fatigue simulation turned on (see below — this needs turning on in Settings too, since TruckHub
  can't tell on its own whether you're using it)
- **Job logbook** — a running record of every job you deliver this session: pickup, drop-off, mileage,
  and payout, with a running total (see "Using the job logbook" below)
- **Live GPS map** — a real, road-following map of wherever you're driving, with a route to your
  current job that reroutes if you go off track (see "The GPS map" below)
- **Extended Instruments (EXT)** — a second panel for everything the main dashboard doesn't have room
  for: oil/water temps, oil pressure, battery voltage, brake air pressure and temp, suspension
  deflection per wheel, and more (see "The EXT window" below)

Every item above can be turned off individually in Settings if you don't want it cluttering the
overlay — the window reflows to fit whatever's left on.

## Installing it

Just run `TruckHub.exe`. That's it.

It automatically finds your ETS2 / ATS install(s) via Steam and installs the small telemetry plugin
it needs — no manual setup required. If a game isn't found automatically (a non-Steam copy, or a
drive Steam doesn't see), open Settings and point it at the install folder yourself.

One thing to check in-game: **Options → Gameplay → HUD → Route Advisor: speed limit** needs to be
turned on, since that's what feeds the speed limit sign.

## Using it

- **Drag** anywhere on the window to move it, and use the **grip in the bottom-right corner** to
  resize it — everything scales to fit, job logbook included (see below).
- The **⛶ button** snaps the window to fill your whole screen; click it again to go back.
- The **☰ button** opens the suggested mod load order (see below).
- The **⚙ button** opens Settings, split into two tabs:
  - **App Configure** — check for updates, point TruckHub at a game install manually, calibrate
    gear numbers for an H-shifter truck, and point TruckHub at your mod folder manually if it's
    somewhere nonstandard
  - **UI Setup** — switch between MPH/KM-H, Tonnes/Lbs, and Litres/Gallons independently of each
    other; turn any HUD element on or off, including the speed sign, in-game clock, odometer, and
    current-speed gauge separately; and turn on the rest timer if you drive with fatigue simulation
    enabled
- **GPS / LOG / EXT** — docked tabs for opening the GPS map, job logbook, and Extended Instruments
  window.

### The GPS map

Click the **GPS** tab to open a real, road-following map — actual roads, cities, and state/country
borders for whichever game you're in, not a generic map with your position pasted on top. It tracks
your live position and heading, and once a job's destination is known, draws a route that actually
follows real roads to get there. The route is calculated once per job rather than constantly
re-routing, the same way most dedicated GPS units work.

Covers the full map for both games, including ETS2's UK — reachable in-game only by ferry — and
routes straight across a ferry or train crossing as part of the same job, the same way it would any
other stretch of the trip. Note that TruckHub's GPS has no connection to the in-game GPS — they're
computed completely independently, so depending on routing they can occasionally disagree with
each other. That's a limitation of what the SCS SDK exposes, not a bug.

On the map itself: real MUTCD-style highway shields (Interstate/US Route/State Route in ATS, green
E-road/motorway/expressway and blue national/regional plates in ETS2), pictogram markers for fuel,
service, rest stops, and weigh stations, and every delivery company's real in-game name and logo at
its marker. ATS also shows a green advance-guide next-turn sign in the map's bottom-right corner
while a route is active — which way the next turn goes and how far off it is — toggleable from the
map's own toolbar (ETS2 support planned).

**LAN Mode**, toggled from the map's own toolbar, lets you view your live position and route from any
browser on your local network — a phone or tablet propped on the dash, for instance. Nothing needs
installing on the other device, and it's read-only: placing a pin or turning LAN Mode on/off only
ever happens from the PC itself.

### The EXT window

Click the **EXT** tab for a second instrument panel covering everything the main dashboard doesn't
have room for: analog gauges for oil pressure, oil temperature, water temperature, battery voltage,
and brake air pressure/temperature; a suspension-deflection readout per wheel on both truck and
trailer, with lift/tag axles clearly labelled; and accessory wear. Every element can be switched on
or off individually from its own settings window, same as the main dashboard.

### Suggested mod load order

ATS/ETS2 both want mods loaded in roughly the right order for best results — get it wrong and you
can see visual glitches or crashes. Click **☰** and hit **Scan** for whichever game you want (ATS
and ETS2 are scanned separately, on demand — TruckHub never scans on its own, only when you ask).
It reads every mod's own manifest (and, where that's not specific enough, its file name) and sorts
them into the community-recommended category order: UI & Menus, Economy & Progression, Sound
Frameworks, Graphics & Season Weather, Physics Systems, Interior & Cabin Accessories, Standalone
Trucks, Paint Jobs & Skins, Wheel & Tire Packs, then Trailers & Cargo. Mods it can't confidently
place show up as "Unsorted" rather than a guess.

**This is a suggestion, not a guarantee.** It doesn't touch, reorder, or enable/disable anything in
your actual Mod Manager — you still apply the order yourself. Some mods are fussy regardless of
category and can still conflict or crash even correctly ordered, so treat this as a solid starting
point, not the last word.

### Calibrating an H-shifter

If you drive with a physical H-shifter, the game doesn't report gear numbers the way you'd expect
from the stick position alone. To fix that: while driving, shift into the gear you want labelled and
press **Ctrl+Alt+G** — this works even while the game is focused. Then open Settings, type the gear
number next to the captured value, and click Set. If your shifter has a separate splitter switch,
do the same again with it engaged. This only needs doing once per truck/gearbox — TruckHub remembers
it and switches to a different truck's calibration (or falls back to raw numbers) automatically.
(Driving an automatic? None of this applies — TruckHub detects it and shows D/N/P/R instead.)

### Using the job logbook

Click the **LOG** tab on the right edge of the window to slide the logbook out. It logs every job
as soon as you deliver it — cancelled jobs aren't recorded — with a running
total of payout and mileage for the session at the top. It's not saved anywhere; closing TruckHub
clears it, so think of it as a running tally for the session you're currently driving, not a permanent
record.

While it's docked, it resizes right along with the main window. If you'd rather have it as its own
window — to put it on a second monitor, or just give it more room — click the **pop-out** button in
its header and it detaches into an independent, freely resizable window with its own close button.
Closing it (either its own close button, or clicking the same LOG tab again on the main window)
puts it away; open it again and it comes back docked.

## Staying up to date

TruckHub quietly checks GitHub for a newer release each time it starts — if you're already on the
latest version, or you're offline, nothing happens and you're never interrupted. If a newer version
is out, Settings shows an **Update available** message with a link straight to the release. You can
also check any time yourself with the **Check for Updates** button at the top of Settings.

## Full manual

For a complete breakdown of every icon, gauge, and colour code — what each one means and exactly
when it lights up — see [TruckHub-Manual.pdf](docs/TruckHub-Manual.pdf).

## Credits & third-party software

TruckHub's telemetry reading is built on [RenCloud/scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin)
(MIT), and the live GPS map is rendered with [MapLibre GL JS](https://github.com/maplibre/maplibre-gl-js)
(BSD-3-Clause) — both distributed as part of TruckHub. The GPS map's underlying tile and route
data were generated using [TruckSim Maps](https://github.com/truckermudgeon/maps) (GPL-3.0) as a
development-time tool, and its routing engine was designed after studying
[TruckNav-Sim](https://github.com/Rares-Muntean/TruckNav-Sim) (GPL-3.0) as a reference, with no
code copied from either. Full license texts and details on exactly what was used and how are in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
