# Third-Party Notices

TruckHub is an independent, fan-made project. It is not affiliated with, endorsed by, or
sponsored by SCS Software. "American Truck Simulator" and "Euro Truck Simulator 2" are
trademarks of SCS Software. TruckHub reads telemetry data that the games themselves publish
through their own official SDK. The GPS map's road/route data is derived geometry, not
game assets - see the TruckSim Maps entry below. The one exception is the GPS map's company
name/logo overlay, which embeds a small set of real logo images extracted directly from the
games' own files - see "SCS Software company logos" below for exactly what and why.

TruckHub builds on the open-source software and public research listed below. Full license
texts are reproduced where the software itself is distributed as part of TruckHub; where a
project was used only as a development-time tool or a design reference, that's noted instead.

## Software distributed with TruckHub

### SCS SDK Client
Source: https://github.com/RenCloud/scs-sdk-plugin
License: MIT (Copyright (c) 2014 Hans)

The telemetry layer that reads live data from ATS/ETS2 (`src/TruckHub/Telemetry/SCSSdkClient/`)
is built on this library. Full license text: `src/TruckHub/Telemetry/SCSSdkClient/LICENSE.txt`.

> The MIT License (MIT)
>
> Copyright (c) 2014 Hans
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of
> this software and associated documentation files (the "Software"), to deal in
> the Software without restriction, including without limitation the rights to
> use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
> the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
> FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
> COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
> IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### MapLibre GL JS
Source: https://github.com/maplibre/maplibre-gl-js (v6.6.0)
License: BSD-3-Clause, plus bundled code from mapbox-gl-js, glfx.js, and d3-color (see below)

Renders the live GPS map (`src/TruckHub/Assets/WebMap/vendor/`). Full license text, including
the bundled third-party notices below it, is shipped alongside the library at
`src/TruckHub/Assets/WebMap/vendor/LICENSE.txt`.

> Copyright (c) 2023, MapLibre contributors
>
> All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification,
> are permitted provided that the following conditions are met:
>
>     * Redistributions of source code must retain the above copyright notice,
>       this list of conditions and the following disclaimer.
>     * Redistributions in binary form must reproduce the above copyright notice,
>       this list of conditions and the following disclaimer in the documentation
>       and/or other materials provided with the distribution.
>     * Neither the name of MapLibre GL JS nor the names of its contributors
>       may be used to endorse or promote products derived from this software
>       without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
> ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
> WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
> DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
> ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
> (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
> LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
> ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
> (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
> SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
>
> MapLibre GL JS also contains code from mapbox-gl-js v1.13 and earlier (BSD-3-Clause,
> Copyright (c) 2020 Mapbox), glfx.js (MIT, Copyright (C) 2011 Evan Wallace), and a portion
> of d3-color (BSD-3-Clause, Copyright 2010-2016 Mike Bostock). See
> `src/TruckHub/Assets/WebMap/vendor/LICENSE.txt` for the complete text of all four notices.

### SCS Software company logos
Source: extracted from the games' own installed files (`material/ui/company/small/*.tobj`),
via `truckermudgeon/maps`' existing texture-decode tooling (see the TruckSim Maps entry below)
Rights: original artwork, © SCS Software

The GPS map's company-name overlay shows each in-game delivery company's real logo alongside
its name. Unlike the road/route data below, these are SCS Software's own original artwork
(fictional in-game company logos, not real-world trademarks - checked against all 498 company
names shipped, no matches), extracted unmodified from a legitimately-owned, fully-DLC'd copy of
each game and embedded in TruckHub (`Assets/WebMap/company-logos.zip`) purely for identification
alongside the map marker they're already attached to in-game. A companion Workshop mod that
reskins these same companies with real-world corporate logos was investigated and deliberately
not integrated, specifically because of the different (and real) trademark exposure that would
carry - see `Assets/WebMap/GENERATION.md`'s company-markers section for the full reasoning.

## Tools and research used during development (not distributed with TruckHub)

### TruckSim Maps
Source: https://github.com/truckermudgeon/maps
License: GPL-3.0-or-later

The ETS2/ATS map tiles and route-graph data embedded in TruckHub (`Assets/WebMap/*tiles.zip`,
`Assets/RouteGraph/*.zip`) were generated by running this project's parsing/tiling tools
against a legally-owned copy of the games' own files. No code from this project is included
in or shipped with TruckHub - only the data our own scripts produced by using it as a tool.
Documented in full in `Assets/WebMap/GENERATION.md` and `GENERATION-Ets2.md`.

### TruckNav-Sim
Source: https://github.com/Rares-Muntean/TruckNav-Sim
License: GPL-3.0

TruckHub's GPS routing engine (`RoutingGraph.cs`, `RoutingService.cs`) was designed after
studying this project's approach to routing over a truck-sim road graph (directed-edge A*,
straight-line heuristic, hard U-turn prevention). No code was copied from it - the algorithm
was implemented independently specifically so TruckHub's own license terms wouldn't be
affected by TruckNav-Sim's GPL-3.0. Credited here as the design inspiration it was.
