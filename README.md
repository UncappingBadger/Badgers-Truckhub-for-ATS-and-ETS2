# TruckHub

A small always-on-top overlay for Euro Truck Simulator 2 / American Truck Simulator that shows the
information you actually want while driving, without alt-tabbing or squinting at the in-cab dash.

## What it shows

- **Speed sign** — the speed limit, shown as a US-style rectangle in ATS or a round European sign in
  ETS2, matching whichever game you're actually in
- **Turn signals** — green arrows either side of the speed sign, flashing in sync with your actual
  indicators; both flash together automatically when hazards are on
- **Lights cluster** — a row of icons for parking brake, sidelights, low beam, high beam, and beacon
  (see "What the icons mean" below)
- **Warning lights** — the same low air pressure / emergency brake / oil pressure / engine temp / low
  fuel / low AdBlue / battery warnings your dashboard has, dimmed until something actually needs
  attention (see "What the icons mean" below)
- **Current speed** — its own gauge with a ring that fills as you speed up, coloured green while
  you're at or under the limit, amber up to 5 over, red beyond that
- **Cruise control** — lit up when it's on, showing the speed it's set to
- **Gear & RPM** — gear in its own box (see H-shifter calibration below for physical shifters), RPM as
  a gauge that fills toward the engine's actual redline and changes colour as it gets close
- **Job info** — route, cargo and weight, payout, distance travelled, time left
- **Fuel, AdBlue & economy** — remaining amount and percentage for both (AdBlue only shown on trucks
  that have it), plus MPG or L/100km based on your actual driving

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
  resize it — everything scales to fit.
- The **⛶ button** snaps the window to fill your whole screen; click it again to go back.
- The **⚙ button** opens Settings, where you can:
  - Switch between MPH/KM-H, Tonnes/Lbs, and Litres/Gallons independently of each other
  - Turn any HUD element on or off
  - Point TruckHub at a game install manually
  - Calibrate gear numbers for an H-shifter truck

### Calibrating an H-shifter

If you drive with a physical H-shifter, the game doesn't report gear numbers the way you'd expect
from the stick position alone. To fix that: while driving, shift into the gear you want labelled and
press **Ctrl+Alt+G** — this works even while the game is focused. Then open Settings, type the gear
number next to the captured value, and click Set. If your shifter has a separate splitter switch,
do the same again with it engaged. This only needs doing once per truck/gearbox — TruckHub remembers
it and switches to a different truck's calibration (or falls back to raw numbers) automatically.

## What the icons mean

TruckHub mirrors real truck dashboard telltales, including the colours — grey/dim always means "off",
a colour means "on". If you don't drive real vehicles for a living, here's what each one is:

### Lights cluster

| Icon | Meaning | Colour when on |
|---|---|---|
| Circle with **P** | Parking brake is engaged | Red |
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
