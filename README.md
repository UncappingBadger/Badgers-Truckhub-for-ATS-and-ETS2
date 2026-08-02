# TruckHub

A small always-on-top overlay for Euro Truck Simulator 2 / American Truck Simulator that shows the
information you actually want while driving, without alt-tabbing or squinting at the in-cab dash.

## What it shows

- **Speed sign** — the speed limit, shown as a US-style rectangle in ATS or a round European sign in
  ETS2, matching whichever game you're actually in
- **Turn signals** — green arrows either side of the speed sign, flashing in sync with your actual
  indicators; both flash together automatically when hazards are on
- **Lights cluster** — a row of icons, braking system first (parking brake, retarder — showing the
  actual engaged level, not just on/off — and engine/Jake brake, grouped together since they're all
  the same system), then sidelights, low beam, high beam, beacon, differential lock, and lift/tag
  axle position (see "What the icons mean" below)
- **In-game clock** — a small digital-clock display directly under the speed limit sign, in green
  monospace digits so it's never mistaken for your PC's actual clock; always visible regardless of
  whether you've got an active job
- **Warning lights** — the same low air pressure / emergency brake / oil pressure / engine temp / low
  fuel / low AdBlue / battery warnings your dashboard has, dimmed until something actually needs
  attention (see "What the icons mean" below)
- **Current speed** — its own gauge with a ring that fills as you speed up, coloured green while
  you're at or under the limit, amber up to 5 over, red beyond that
- **Cruise control** — lit up when it's on, showing the speed it's set to
- **Gear & RPM** — gear in its own box, showing the actual gear number for manual/H-shifter trucks
  (see H-shifter calibration below) or **D / N / P / R** automatically if you're driving an automatic;
  RPM as a gauge that fills toward the engine's actual redline and changes colour as it gets close
- **Job info** — route, cargo and weight, payout, distance travelled, time left
- **Fuel & AdBlue gauge** — a real analogue-style dial for fuel with a needle that sweeps to the actual
  level (not a snap-to-value jump), plus a 4-segment level bar for AdBlue built into the same gauge
  face on trucks that have one (see "What the icons mean" below for how to read it)
- **Trip Needs & Economy** — what percentage of your fuel tank the rest of this trip will use (green if
  you've got enough, red if you don't), plus your actual MPG or L/100km based on real driving, not a
  spec-sheet number
- **Rest timer** *(optional)* — time left before you need a break, for players who drive with the
  fatigue simulation turned on (see below — this needs turning on in Settings too, since TruckHub
  can't tell on its own whether you're using it)
- **Job logbook** — a running record of every job you deliver this session: pickup, drop-off, mileage,
  and payout, with a running total. Tucked away behind a small arrow tab so it's out of the way until
  you want it (see "Using the job logbook" below)

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
- The **⚙ button** opens Settings, split into two tabs:
  - **App Configure** — check for updates, point TruckHub at a game install manually, and calibrate
    gear numbers for an H-shifter truck
  - **UI Setup** — switch between MPH/KM-H, Tonnes/Lbs, and Litres/Gallons independently of each
    other; turn any HUD element on or off, including the speed sign, in-game clock, and
    current-speed gauge separately; and turn on the rest timer if you drive with fatigue simulation
    enabled

### Calibrating an H-shifter

If you drive with a physical H-shifter, the game doesn't report gear numbers the way you'd expect
from the stick position alone. To fix that: while driving, shift into the gear you want labelled and
press **Ctrl+Alt+G** — this works even while the game is focused. Then open Settings, type the gear
number next to the captured value, and click Set. If your shifter has a separate splitter switch,
do the same again with it engaged. This only needs doing once per truck/gearbox — TruckHub remembers
it and switches to a different truck's calibration (or falls back to raw numbers) automatically.
(Driving an automatic? None of this applies — TruckHub detects it and shows D/N/P/R instead.)

### Using the job logbook

Click the small **arrow** on the right edge of the window (about halfway down) to slide the logbook
out. It logs every job as soon as you deliver it — cancelled jobs aren't recorded — with a running
total of payout and mileage for the session at the top. It's not saved anywhere; closing TruckHub
clears it, so think of it as a running tally for the session you're currently driving, not a permanent
record.

While it's docked, it resizes right along with the main window. If you'd rather have it as its own
window — to put it on a second monitor, or just give it more room — click the **pop-out** button in
its header and it detaches into an independent, freely resizable window with its own close button.
Closing it (either its own close button, or clicking the same arrow tab again on the main window)
puts it away; open it again and it comes back docked.

## Staying up to date

TruckHub quietly checks GitHub for a newer release each time it starts — if you're already on the
latest version, or you're offline, nothing happens and you're never interrupted. If a newer version
is out, Settings shows an **Update available** message with a link straight to the release. You can
also check any time yourself with the **Check for Updates** button at the top of Settings.

## What the icons mean

TruckHub mirrors real truck dashboard telltales, including the colours — grey/dim always means "off",
a colour means "on". If you don't drive real vehicles for a living, here's what each one is:

### Lights cluster

| Icon | Meaning | Colour when on |
|---|---|---|
Parking brake, retarder, and engine brake are grouped together first, since they're all the same
braking system - just different use cases:

| Icon | Meaning | Colour when on |
|---|---|---|
| Circle with **P** | Parking brake is engaged | Red |
| Circle with **R** | Retarder - only shown on trucks that actually have one. The letter itself swaps to the current level number while engaged | Green, showing the engaged level (1, 2, 3...); dim **R** when off |
| Circle with **E** / **J** | Engine brake is on - labelled **E** in ETS2, **J** (Jake brake) in ATS | Green |
| Small lamp (dot in a circle) | Sidelights / parking lights are on | Green |
| Lamp with angled beams | Low beam headlights are on | Green |
| Lamp with straight beams | High beam headlights are on | Blue |
| Dome with rays | Roof beacon (the amber warning light some trucks carry) is on | Amber |
| Padlock | Differential lock is engaged (locks the axle so both wheels spin together - used for extra grip in mud/snow/ice) | Amber |
| Wheel with an up-arrow | Lift/tag axle position - only shown on trucks that actually have one | Amber = axle lifted up; dim = lowered (normal, full load) |

### Warning cluster

These only light up when something is actually wrong — everything dim means everything's fine.

| Icon | Meaning | Colour when tripped |
|---|---|---|
| Circle with **!** | Air pressure in the brake system is low | Red |
| Circle with **P** | Emergency brake has auto-applied because air pressure got critically low (different from the parking brake icon above — this one means a fault, not you choosing to park) | Red |
| Oil can | Oil pressure is low | Red |
| Thermometer | Engine coolant is overheating | Red |
| Fuel pump | Fuel is running low | Amber |
| Droplet | AdBlue is running low | Amber |
| Battery | Charging system fault — you could lose electrical power | Red |

Red always means "safety-critical, deal with it soon"; amber means "needs attention, not urgent yet".

### Fuel & AdBlue gauge

Built to read like a real combined gauge, not just a number:

- The **needle** sweeps between **E** (empty) and **F** (full) as your fuel level actually changes —
  it eases toward the real reading rather than jumping to it, the same way a physical gauge would.
  The **red arc** near E marks roughly the last eighth of the tank, matching where most real vehicles'
  low-fuel warning kicks in.
- The small **light near "E"** is the low-fuel warning — same idea as the light on a real dashboard,
  separate from the needle itself (a real gauge needle doesn't change colour when you're running low).
- On trucks with AdBlue, the **4-segment bar** below the needle works the same way: each segment lights
  as the level reaches into that quarter, with its own small warning light near its own "E".
- Both the needle and the AdBlue bar sit pinned at empty whenever your truck's electrics are off (key
  out) — that's deliberate, matching how a real gauge has no power of its own to hold a reading without
  the ignition on.
