function reportToHost(level, message) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage({ type: 'jsLog', level, message: String(message) });
  }
}

window.addEventListener('error', e => reportToHost('error', `${e.message} @ ${e.filename}:${e.lineno}`));
window.addEventListener('unhandledrejection', e => reportToHost('error', `unhandled rejection: ${e.reason}`));

// Caps the map's render rate by throttling requestAnimationFrame itself, deferring (not dropping)
// callbacks that arrive sooner than the target interval allows - MapLibre's own render loop, like
// any WebGL app, schedules its next repaint via rAF, which otherwise just tracks the display's own
// refresh rate (60Hz, 144Hz, whatever the monitor does) with no cap of its own. Has to be patched
// here, before MapLibre's own module import resolves below, not after - if MapLibre caches its own
// reference to window.requestAnimationFrame at module load (a common perf pattern), patching it
// later would silently do nothing. window.__setMapFps lets the rest of this file (and a message
// from the C# side, see below) change the target after the fact - starts at 30 by default, GpsCoordinator
// can push it up to 60 if CoreBalancer sees real system headroom. Remote LAN viewers never get that
// push (no bridge to receive it over), so they stay at the conservative default - a reasonable
// choice for an unknown device's own battery/thermals anyway.
window.__mapTargetFps = 30;
(function throttleRequestAnimationFrame() {
  const nativeRAF = window.requestAnimationFrame.bind(window);
  let lastFrameTime = 0;
  window.requestAnimationFrame = function (callback) {
    return nativeRAF(function (now) {
      const minInterval = 1000 / window.__mapTargetFps;
      if (now - lastFrameTime >= minInterval) {
        lastFrameTime = now;
        callback(now);
      } else {
        window.requestAnimationFrame(callback);
      }
    });
  };
})();

// This same page is served two ways: locally inside WebView2 (window.chrome.webview exists, a
// real bidirectional postMessage bridge to GpsMapWindow) and remotely to a plain browser on the
// LAN via GpsLanServer (no such bridge - just a static file server, see LAN Mode's own read-only
// reasoning further down). Needed this early (not just further down where it's used for
// pin/LAN-button visibility) because the two cases learn which game's data to load in different
// ways - see resolveActiveGame() below.
const isRemote = !(window.chrome && window.chrome.webview);

// Which game's map data to load. Locally, GpsMapWindow's own Navigate() call sets a `?game=`
// param on the page URL - read synchronously, no round trip needed. Remotely, there's no
// equivalent: the LAN URL shown to the user (typed by hand into another device's browser) stays
// a plain host:port specifically so there's nothing extra to mistype, so a remote page instead
// asks GpsLanServer's own `/api/game` endpoint, computed there from the same live telemetry
// `/api/position` already uses. Either way this has to resolve before the style.json/tiles
// fetches below, not over postMessage afterward - a message arriving once the page's listener is
// registered can't retroactively change which files already got fetched. Falls back to ATS's
// folders on anything unexpected (missing param, a `/api/game` fetch failure) rather than
// fetching nothing.
async function resolveActiveGame() {
  if (!isRemote) {
    return new URLSearchParams(window.location.search).get('game') === 'ets2' ? 'ets2' : 'ats';
  }
  try {
    const data = await fetch('/api/game').then(r => r.json());
    return data && data.game === 'ets2' ? 'ets2' : 'ats';
  } catch {
    return 'ats';
  }
}

import('./vendor/maplibre-gl.mjs')
  .then(async maplibregl => {
    const { Map, Marker, NavigationControl } = maplibregl;
    const activeGame = await resolveActiveGame();
    const TILES_DIR = activeGame === 'ets2' ? 'ets2-tiles' : 'tiles';
    const MAPDATA_DIR = activeGame === 'ets2' ? 'ets2-mapdata' : 'mapdata';
    reportToHost('info', `maplibre module loaded (game=${activeGame})`);

    // The vector tile source's URL can't just be a plain relative path in style.json (like the
    // geojson sources below use) or a hardcoded "https://truckhub.app/..." one - MapLibre resolves
    // tile URLs in a different context than the page itself (its own tile-loading worker, going by
    // the error), where relative resolution against the page's own location silently fails
    // ("Failed to fetch (0): tiles/7/27/47.pbf" - the literal unresolved relative string, not even
    // a malformed absolute URL), and a hardcoded WebView2-only hostname obviously doesn't exist for
    // a real browser loading this same page remotely over LAN Mode. Fetching style.json ourselves
    // and rewriting the tiles URL to window.location.origin - the one thing that's always correct
    // in both contexts (the WebView2 virtual host locally, this device's real address remotely) -
    // sidesteps the whole question instead of hoping either kind of path resolves right.
    //
    // Every mapdata-backed source gets the same TILES_DIR/MAPDATA_DIR swap - style.json's own
    // structure is shared between both games (down to the vector tiles' internal "ats" layer
    // name, which tile-ats.mjs always writes regardless of which map's data went in), only the
    // folder these fetches point at differs.
    const style = await fetch('style.json').then(r => r.json());
    style.sources.ats.tiles = [`${window.location.origin}/${TILES_DIR}/{z}/{x}/{y}.pbf`];
    // Same real vector tiles as the roads above now (previously a flat GeoJSON blob - see
    // TruckHub.csproj's own comment on why: MapLibre had to hold and re-evaluate every single
    // facility/sign feature in the whole dataset on every pan regardless of what was on screen,
    // which is what made dragging feel clunky), so these need the same absolute-URL treatment the
    // comment above explains, not the plain relative-path .data fetch a GeoJSON source could use.
    style.sources['road-signs'].tiles = [`${window.location.origin}/${TILES_DIR}/road-signs/{z}/{x}/{y}.pbf`];
    style.sources['poi-facilities'].tiles = [`${window.location.origin}/${TILES_DIR}/poi-facilities/{z}/{x}/{y}.pbf`];
    style.sources['company-logos'].tiles = [`${window.location.origin}/${TILES_DIR}/company-logos/{z}/{x}/{y}.pbf`];
    style.sources['town-labels'].data = `${MAPDATA_DIR}/town-labels.json`;
    style.sources['state-labels'].data = `${MAPDATA_DIR}/state-labels.json`;

    // Road color/width by class (freeway/arterial/local) - a road feature's own `lookToken`
    // property (already present in the tiles) maps to a class via road-classes.json
    // (extract-map-features.ts, same freeway/motorway vs expressway/dividedRoad vs
    // slowRoad/localRoad classification already used for the routing graph's time-based edge
    // weights - real road hierarchy, not a guess). Built as a MapLibre `match` expression here
    // rather than baked into the tiles themselves, same reasoning as the tiles-URL patch above:
    // one place to generate style.json's dynamic bits, from data that's cheap to ship as its own
    // small file.
    const roadClasses = await fetch(`${MAPDATA_DIR}/road-classes.json`).then(r => r.json());
    const ROAD_CLASS_COLOR = { freeway: '#FFB454', arterial: '#7FA8D9', local: '#c7cdd6' };
    const ROAD_CLASS_WIDTH = { freeway: [0.7, 1.8, 4.2], arterial: [0.55, 1.3, 3.2], local: [0.4, 1, 2.6] };
    function buildRoadClassExpression(valueForClass) {
      const expr = ['match', ['get', 'lookToken']];
      for (const [token, cls] of Object.entries(roadClasses)) {
        expr.push(token, valueForClass(cls));
      }
      expr.push(valueForClass('local'));
      return expr;
    }
    const roadsLayer = style.layers.find(l => l.id === 'roads');
    if (roadsLayer) {
      roadsLayer.paint['line-color'] = buildRoadClassExpression(cls => ROAD_CLASS_COLOR[cls] ?? ROAD_CLASS_COLOR.local);
      roadsLayer.paint['line-width'] = [
        'interpolate', ['linear'], ['zoom'],
        4, buildRoadClassExpression(cls => (ROAD_CLASS_WIDTH[cls] ?? ROAD_CLASS_WIDTH.local)[0]),
        8, buildRoadClassExpression(cls => (ROAD_CLASS_WIDTH[cls] ?? ROAD_CLASS_WIDTH.local)[1]),
        13, buildRoadClassExpression(cls => (ROAD_CLASS_WIDTH[cls] ?? ROAD_CLASS_WIDTH.local)[2]),
      ];
    }

    let follow = false;
    let pinModeActive = false;
    let marker = null;
    let pinMarker = null;
    let lastPos = null;

    // isRemote itself is declared up top now (resolveActiveGame() needed it before this callback
    // even starts) - kept here as a reminder of what it gates below: remote view is deliberately
    // read-only, same reasoning as ZoidHub's own LAN Mode - editing (placing/clearing a pin,
    // toggling LAN Mode itself) only ever happens from the PC's own WebView2 instance, never from
    // a device anyone on the WiFi could be holding.

    const followBtn = document.getElementById('follow-btn');
    const pinBtn = document.getElementById('pin-btn');
    const clearPinBtn = document.getElementById('clear-pin-btn');
    const lanBtn = document.getElementById('lan-btn');
    const turnsBtn = document.getElementById('turns-btn');
    const turnSignCanvas = document.getElementById('turn-sign');
    const lanStatus = document.getElementById('lan-status');
    const lanSslPrompt = document.getElementById('lan-ssl-prompt');
    const lanNoSslBtn = document.getElementById('lan-no-ssl-btn');
    const lanUseSslBtn = document.getElementById('lan-use-ssl-btn');

    // On by default - a purely local display preference, not something that needs an opt-in the
    // way a networking feature like LAN Mode does.
    let turnsEnabled = true;
    turnsBtn.classList.add('active');
    let lastTurn = null;

    if (isRemote) {
      pinBtn.style.display = 'none';
      clearPinBtn.style.display = 'none';
      lanBtn.style.display = 'none';
      // Same "editing is PC-only, viewing is fine remotely" split as the buttons above - the sign
      // itself still renders for a remote viewer if the PC currently has it toggled on, this just
      // hides the toggle control itself.
      turnsBtn.style.display = 'none';

      // Keeps the device's screen from locking while the map is being watched (e.g. mounted in a
      // vehicle) - supported on iPhone Safari (16.4+) and Android Chrome. A wake lock is released
      // automatically by the OS whenever the page goes hidden (tab backgrounded, screen manually
      // locked), so it has to be re-requested on every return to visibility, not just once on load.
      // Silently does nothing on browsers without Wake Lock API support at all - no real fallback
      // exists for those.
      let wakeLock = null;
      const requestWakeLock = async () => {
        if (!('wakeLock' in navigator)) return;
        try {
          wakeLock = await navigator.wakeLock.request('screen');
        } catch {
          // Denied or unsupported in this context - nothing actionable.
        }
      };
      requestWakeLock();
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') requestWakeLock();
      });
    }

    followBtn.addEventListener('click', () => {
      follow = !follow;
      followBtn.classList.toggle('active', follow);
      if (follow && lastPos) {
        map.easeTo({ center: [lastPos.lon, lastPos.lat], duration: 400 });
      }
    });

    turnsBtn.addEventListener('click', () => {
      turnsEnabled = !turnsEnabled;
      turnsBtn.classList.toggle('active', turnsEnabled);
      renderTurnSign();
    });

    pinBtn.addEventListener('click', () => {
      pinModeActive = true;
      pinBtn.classList.add('active');
      map.getCanvas().style.cursor = 'crosshair';
    });

    // Removes the placed pin and asks the C# side to fall back to whatever route it would show
    // without a manual override - the current job's route if one's active, or nothing at all.
    clearPinBtn.addEventListener('click', () => {
      if (pinMarker) {
        pinMarker.remove();
        pinMarker = null;
      }
      setRoute([]);
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ type: 'clearPin' });
      }
    });

    // Turning LAN Mode off never needs the SSL choice - just toggle it off directly. Turning it on
    // asks first (see the prompt buttons below): Apple device users need plain HTTP (see
    // GpsLanServer's own comment for why a self-signed cert fails there), everyone else can opt
    // into an encrypted connection if they'd rather.
    lanBtn.addEventListener('click', () => {
      if (!(window.chrome && window.chrome.webview)) return;
      if (lanBtn.classList.contains('active')) {
        window.chrome.webview.postMessage({ type: 'toggleLan' });
        return;
      }
      lanSslPrompt.classList.add('visible');
    });

    lanNoSslBtn.addEventListener('click', () => {
      lanSslPrompt.classList.remove('visible');
      window.chrome.webview.postMessage({ type: 'toggleLan', useSsl: false });
    });

    lanUseSslBtn.addEventListener('click', () => {
      lanSslPrompt.classList.remove('visible');
      window.chrome.webview.postMessage({ type: 'toggleLan', useSsl: true });
    });

    // Initial camera before the first live position arrives and jumpTo() below takes over -
    // roughly the geographic center of whichever map is active, so the brief moment before that
    // first position update shows the right continent (and doesn't waste a burst of tile
    // requests on the other one entirely). Confirmed live: with this still hardcoded to ATS's own
    // center, opening the map for an ETS2 session started the camera over the Atlantic/North
    // America, generating tile-fetch 404s for a region with no ETS2 data at all before the real
    // jump to the truck's position corrected it a moment later - harmless in practice (self-heals
    // before a user would notice) but pointless log noise and wasted requests.
    const [initialLon, initialLat] = activeGame === 'ets2' ? [15, 50] : [-96, 39];
    const map = new Map({
      container: 'map',
      style,
      center: [initialLon, initialLat],
      zoom: 4.2,
      attributionControl: false,
      // Default cache sizing tracks roughly the current viewport's own tile count, not everything
      // fetched this session - so the wide initial whole-map view's tiles were getting evicted
      // once the user zoomed in and drove around for a while, meaning zooming back out later had
      // to refetch them fresh (the stutter this is fixing). All tiles here are small and served
      // from local disk (see WebMapAssetExtractor), so holding onto far more than the bare minimum
      // is cheap - conservative starting point, easy to tune down later if it isn't needed.
      maxTileCacheSize: 2000,
      // This map is almost entirely thin lines (roads, state borders) - exactly the content
      // antialiasing (WebGL's default MSAA-style edge smoothing) costs the most GPU work on, for
      // a screen full of long straight/gently-curved segments where the jaggies it's smoothing
      // are barely noticeable at map zoom levels anyway. Off by default in most WebGL contexts
      // already, but MapLibre requests it explicitly - turning it back off here trades a small,
      // hard-to-notice amount of edge crispness for real GPU savings, on top of the pixelRatio
      // scaling below.
      antialias: false,
    });

    // WebGL rendering cost scales with actual rendered pixel count (CSS area * pixelRatio^2), not
    // window state - going fullscreen/maximized doesn't just make the map look bigger, it makes
    // MapLibre render several times as many pixels every frame (more tiles visible too), which was
    // confirmed live as the actual cause of "GPS window fullscreen makes the whole app sluggish"
    // (not driving/route updates - those were already fixed, and this window's normal size was
    // already confirmed fine). Keep full native sharpness at/near the normal windowed size, and
    // only scale rendering resolution down once the window grows meaningfully beyond that, so the
    // rendered pixel count stays roughly capped instead of ballooning linearly with window area.
    const BASELINE_CSS_PIXELS = 900 * 650;
    function adjustPixelRatio() {
      const cssPixels = window.innerWidth * window.innerHeight;
      const nativeRatio = window.devicePixelRatio || 1;
      if (cssPixels <= BASELINE_CSS_PIXELS * 1.3) {
        map.setPixelRatio(nativeRatio);
        return;
      }
      // Confirmed live across three data points: 0.75 read fine but wasn't enough of a performance
      // cut; 0.6 was *still* confirmed unreadable, not just 0.4 - this map's thin road lines and
      // small canvas-drawn labels turn to mush well before the resolution drops as far as the sqrt
      // area-matching formula wants to take it, especially at 4K-class sizes. Backed off hard to
      // 0.85 - readability takes priority, and the real performance work now lives elsewhere
      // (antialias off above, requestAnimationFrame capped to window.__mapTargetFps below, and
      // CoreBalancer keeping this map's own renderer/GPU processes off the busiest cores) rather
      // than leaning on blurring the whole picture.
      const scale = Math.sqrt(BASELINE_CSS_PIXELS / cssPixels);
      map.setPixelRatio(Math.max(0.85, Math.min(nativeRatio, nativeRatio * scale)));
    }
    adjustPixelRatio();
    window.addEventListener('resize', adjustPixelRatio);

    // Small canvas-drawn label/icon bitmaps registered via map.addImage() - MapLibre's own
    // text-field system needs real glyph PBF tiles (a separate font-rasterization pipeline this
    // build doesn't have; style.json originally had "glyphs": null, which is invalid and was
    // removed entirely, so there was never any text-rendering capability here at all). Drawing
    // labels as plain 2D canvas images with the browser's own font rendering sidesteps that
    // completely. Facility icons are a small known set (registered once, below, on map load);
    // city names and road-sign numbers are effectively unbounded strings, so those are drawn
    // lazily via 'styleimagemissing' the first time each one is actually needed - MapLibre caches
    // added images by id afterward, so each unique label only gets drawn once no matter how many
    // features on screen share it.
    function drawBadge(text, diameter, bgColor, textColor) {
      const scale = 2;
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = diameter * scale;
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);
      ctx.beginPath();
      ctx.arc(diameter / 2, diameter / 2, diameter / 2 - 1, 0, Math.PI * 2);
      ctx.fillStyle = bgColor;
      ctx.fill();
      ctx.strokeStyle = '#12161c';
      ctx.lineWidth = 1.2;
      ctx.stroke();
      ctx.fillStyle = textColor;
      ctx.font = `bold ${Math.round(diameter * 0.5)}px system-ui, sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(text, diameter / 2, diameter / 2 + 0.5);
      // map.addImage() doesn't accept a raw canvas - ImageData, ImageBitmap, HTMLImageElement, or
      // a {width,height,data} object only.
      return { imageData: ctx.getImageData(0, 0, canvas.width, canvas.height), scale };
    }

    // Real pictograms instead of a plain letter-in-circle badge, for the three POI types the user
    // actually navigates to mid-drive (fuel/service/rest) - matching the recognizable-at-a-glance
    // icon language ATS/ETS2's own world map and highway signage use (pump/wrench/bed silhouettes),
    // the same "replicate the real thing, not a generic badge" reasoning as drawShield() below.
    // Weigh station keeps its plain "W" badge - not part of this ask.
    function drawIconBadge(kind, diameter, bgColor, iconColor) {
      const scale = 2;
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = diameter * scale;
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);
      ctx.beginPath();
      ctx.arc(diameter / 2, diameter / 2, diameter / 2 - 1, 0, Math.PI * 2);
      ctx.fillStyle = bgColor;
      ctx.fill();
      ctx.strokeStyle = '#12161c';
      ctx.lineWidth = 1.2;
      ctx.stroke();

      ctx.fillStyle = iconColor;
      ctx.strokeStyle = iconColor;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';

      if (kind === 'fuel') {
        // Dashboard-style fuel icon: tank body + filler cap + hose looping down to a nozzle tip,
        // with two level ticks inside the tank.
        ctx.lineWidth = 1.4;
        ctx.strokeRect(6.5, 7, 5, 9);
        ctx.fillRect(7.5, 4.5, 3, 2.2);
        ctx.beginPath();
        ctx.moveTo(11.5, 9);
        ctx.lineTo(13.5, 9);
        ctx.lineTo(13.5, 14.5);
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(13.5, 15.3, 1, 0, Math.PI * 2);
        ctx.fill();
        ctx.lineWidth = 0.9;
        ctx.beginPath();
        ctx.moveTo(7.3, 10.5);
        ctx.lineTo(10.7, 10.5);
        ctx.moveTo(7.3, 12.5);
        ctx.lineTo(10.7, 12.5);
        ctx.stroke();
      } else if (kind === 'wrench') {
        // Open-end wrench: a diagonal bar with a C-shaped head at each end - the universal
        // repair/service pictogram (also matches a truck dashboard's own "service required" icon).
        ctx.save();
        ctx.translate(diameter / 2, diameter / 2);
        ctx.rotate(-Math.PI / 4);
        ctx.lineWidth = 2.6;
        ctx.beginPath();
        ctx.moveTo(-5.5, 0);
        ctx.lineTo(5.5, 0);
        ctx.stroke();
        ctx.lineWidth = 1.6;
        ctx.beginPath();
        ctx.arc(-6.5, 0, 2.6, Math.PI * 0.25, Math.PI * 1.75);
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(6.5, 0, 2.6, Math.PI * 1.25, Math.PI * 2.75);
        ctx.stroke();
        ctx.restore();
      } else if (kind === 'bed') {
        // Headboard + mattress + pillow - the standard "lodging/rest" pictogram real highway signs
        // use, matching what "rest stop" means here (a parking_ico location a driver can actually
        // stop and sleep at, per GENERATION.md's own note on that category).
        ctx.lineWidth = 1.4;
        ctx.strokeRect(4.5, 12, 11, 3.5);
        ctx.beginPath();
        ctx.moveTo(4.5, 12);
        ctx.lineTo(4.5, 6.5);
        ctx.stroke();
        ctx.beginPath();
        if (ctx.roundRect) {
          ctx.roundRect(5.3, 8.3, 4, 3, 1);
        } else {
          ctx.rect(5.3, 8.3, 4, 3);
        }
        ctx.fill();
      }

      return { imageData: ctx.getImageData(0, 0, canvas.width, canvas.height), scale };
    }

    // Real MUTCD route-sign shapes (Interstate M1-1, US Route M1-4, State Route M1-5) - these are
    // US federal/state highway-standard designs, government works with no copyright, so the actual
    // shapes/colors are replicated directly rather than the earlier v1's generic
    // same-shape-different-border-color badges (which didn't read as "real highway shields" at
    // all).
    function drawShield(number, shieldType) {
      const scale = 2;
      const fontSize = 12;
      const measureCtx = document.createElement('canvas').getContext('2d');
      measureCtx.font = `bold ${fontSize}px system-ui, sans-serif`;
      const width = Math.max(24, measureCtx.measureText(number).width + 12);
      const height = shieldType === 'interstate' ? width * 0.92 : shieldType === 'usRoute' ? width * 0.88 : width;
      const canvas = document.createElement('canvas');
      canvas.width = width * scale;
      canvas.height = height * scale;
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);
      const w = width, h = height;

      ctx.beginPath();
      if (shieldType === 'interstate') {
        // Classic shield silhouette: flat-ish top, bulging sides, pointed bottom.
        ctx.moveTo(w * 0.1, 0);
        ctx.lineTo(w * 0.9, 0);
        ctx.quadraticCurveTo(w, 0, w, h * 0.15);
        ctx.quadraticCurveTo(w, h * 0.55, w * 0.5, h);
        ctx.quadraticCurveTo(0, h * 0.55, 0, h * 0.15);
        ctx.quadraticCurveTo(0, 0, w * 0.1, 0);
      } else if (shieldType === 'usRoute') {
        // Cutout shield: notched top corners, rounded bottom corners, flat bottom.
        ctx.moveTo(w * 0.15, 0);
        ctx.lineTo(w * 0.85, 0);
        ctx.lineTo(w, h * 0.25);
        ctx.lineTo(w, h * 0.82);
        ctx.quadraticCurveTo(w, h, w * 0.82, h);
        ctx.lineTo(w * 0.18, h);
        ctx.quadraticCurveTo(0, h, 0, h * 0.82);
        ctx.lineTo(0, h * 0.25);
      } else {
        // State route: plain circle, the common generic default most states without a bespoke
        // marker shape use.
        ctx.arc(w / 2, h / 2, Math.min(w, h) / 2 - 0.8, 0, Math.PI * 2);
      }
      ctx.closePath();

      // Real AASHTO/FHWA Interstate shield colors are a much darker navy and brighter red than
      // what was here before (#1e3f8f/#b0202a) - that's part of what makes the real shield read at
      // a glance from a moving vehicle; a duller approximation loses exactly the contrast the
      // design is built around.
      const fill = shieldType === 'interstate' ? '#00205b' : '#ffffff';
      const border = shieldType === 'interstate' ? '#ffffff' : '#12161c';
      ctx.fillStyle = fill;
      ctx.fill();
      ctx.strokeStyle = border;
      ctx.lineWidth = shieldType === 'interstate' ? 1.1 : 1.5;
      ctx.stroke();

      if (shieldType === 'interstate') {
        // Red band across the upper third - the top edge is flat/near-full-width there, so a
        // plain rect reads correctly without needing to clip to the shield's own curve.
        ctx.fillStyle = '#c8102e';
        ctx.fillRect(w * 0.08, h * 0.1, w * 0.84, h * 0.22);
      }

      ctx.fillStyle = shieldType === 'interstate' ? '#ffffff' : '#12161c';
      ctx.font = `bold ${fontSize}px system-ui, sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const textY = shieldType === 'interstate' ? h * 0.62 : h / 2 + 0.5;
      ctx.fillText(number, w / 2, textY);
      return { imageData: ctx.getImageData(0, 0, canvas.width, canvas.height), scale };
    }

    // European route-number plates (E-roads, national motorways/expressways, national/regional
    // roads) - unlike the US MUTCD shapes above, most EU countries just use a plain colored
    // rectangle for their route markers; the color carries the classification, not a bespoke
    // silhouette, so one shape with a tier-driven fill color covers the real thing accurately.
    // `tier` is 'motorway' (green - E-roads and national motorways/expressways) or 'national'
    // (blue - everything else, see extract-map-features.ts for the full country-aware mapping).
    function drawEuShield(number, tier) {
      const scale = 2;
      const fontSize = 12;
      const measureCtx = document.createElement('canvas').getContext('2d');
      measureCtx.font = `bold ${fontSize}px system-ui, sans-serif`;
      const width = Math.max(30, measureCtx.measureText(number).width + 14);
      const height = width * 0.62;
      const canvas = document.createElement('canvas');
      canvas.width = width * scale;
      canvas.height = height * scale;
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);
      const w = width, h = height;
      const r = h * 0.22;

      ctx.beginPath();
      ctx.moveTo(r, 0);
      ctx.lineTo(w - r, 0);
      ctx.quadraticCurveTo(w, 0, w, r);
      ctx.lineTo(w, h - r);
      ctx.quadraticCurveTo(w, h, w - r, h);
      ctx.lineTo(r, h);
      ctx.quadraticCurveTo(0, h, 0, h - r);
      ctx.lineTo(0, r);
      ctx.quadraticCurveTo(0, 0, r, 0);
      ctx.closePath();

      ctx.fillStyle = tier === 'motorway' ? '#0a7a3c' : '#0055a4';
      ctx.fill();
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 1.3;
      ctx.stroke();

      ctx.fillStyle = '#ffffff';
      ctx.font = `bold ${fontSize}px system-ui, sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(number, w / 2, h / 2 + 0.5);
      return { imageData: ctx.getImageData(0, 0, canvas.width, canvas.height), scale };
    }

    // Next-turn sign: real US advance-guide-sign styling (green - highway exit/turn guidance -
    // distinct from the Interstate shield's navy blue, which is reserved for route markers) with a
    // bold MUTCD-style advance-turn-arrow glyph and a distance readout, approved against a rendered
    // mockup before this was wired in. Drawn straight onto the fixed #turn-sign canvas (a plain
    // screen-space overlay, not a MapLibre-anchored icon - it always sits in the same map-window
    // corner regardless of pan/zoom) rather than through the addImage/styleimagemissing path the
    // shields above use, since this isn't tied to a map coordinate at all.
    function drawTurnSign(isRight, distanceText) {
      const dpr = window.devicePixelRatio || 1;
      const w = 92, h = 78;
      turnSignCanvas.width = w * dpr;
      turnSignCanvas.height = h * dpr;
      turnSignCanvas.style.width = w + 'px';
      turnSignCanvas.style.height = h + 'px';
      const ctx = turnSignCanvas.getContext('2d');
      ctx.clearRect(0, 0, turnSignCanvas.width, turnSignCanvas.height);
      ctx.scale(dpr, dpr);

      const r = 6;
      ctx.beginPath();
      ctx.moveTo(r, 0);
      ctx.lineTo(w - r, 0);
      ctx.quadraticCurveTo(w, 0, w, r);
      ctx.lineTo(w, h - r);
      ctx.quadraticCurveTo(w, h, w - r, h);
      ctx.lineTo(r, h);
      ctx.quadraticCurveTo(0, h, 0, h - r);
      ctx.lineTo(0, r);
      ctx.quadraticCurveTo(0, 0, r, 0);
      ctx.closePath();
      ctx.fillStyle = '#006747';
      ctx.fill();
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 2;
      ctx.stroke();

      ctx.save();
      ctx.translate(w / 2, 30);
      ctx.fillStyle = '#ffffff';
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 9;
      ctx.lineCap = 'butt';
      ctx.lineJoin = 'miter';

      const sign = isRight ? 1 : -1;
      ctx.beginPath();
      ctx.moveTo(sign * -16, 16);
      ctx.lineTo(sign * -16, 0);
      ctx.quadraticCurveTo(sign * -16, -16, sign * 0, -16);
      ctx.lineTo(sign * 14, -16);
      ctx.stroke();
      ctx.save();
      ctx.translate(sign * 14, -16);
      ctx.rotate(sign === 1 ? Math.PI / 2 : -Math.PI / 2);
      ctx.beginPath();
      ctx.moveTo(-9, 5);
      ctx.lineTo(0, -9);
      ctx.lineTo(9, 5);
      ctx.closePath();
      ctx.fill();
      ctx.restore();
      ctx.restore();

      ctx.fillStyle = '#ffffff';
      ctx.font = 'bold 15px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(distanceText, w / 2, h - 14);
    }

    function renderTurnSign() {
      if (turnsEnabled && lastTurn) {
        drawTurnSign(lastTurn.isRight, lastTurn.distance);
        turnSignCanvas.classList.add('visible');
      } else {
        turnSignCanvas.classList.remove('visible');
      }
    }

    function drawCityLabel(name, scaleRank) {
      const scale = 2;
      const fontSize = Math.max(9, Math.round(16 - scaleRank * 1.1));
      const fontWeight = scaleRank <= 2 ? '700' : scaleRank <= 6 ? '600' : '500';
      // Major cities render brighter/bolder than small towns - the size difference alone reads
      // weakly at a glance, color contrast makes the hierarchy obvious immediately.
      const color = scaleRank <= 2 ? '#f5f7fa' : scaleRank <= 6 ? '#d5dae2' : '#9aa4b2';
      // A separate, very small "cities" circle layer used to mark the actual position - too
      // faint/small against the road-heavy map to read as a marker at all, just looked like
      // floating text with nothing anchoring it. Baking a solid dot into the same bitmap as the
      // text (drawn once, together) fixes that - the marker and label are now visually one unit,
      // and the dot sits almost exactly on the feature's real point (style.json's icon-offset was
      // dropped to [2, 0] to match).
      const dotRadius = Math.max(2, Math.round(4.2 - scaleRank * 0.24));
      const measureCtx = document.createElement('canvas').getContext('2d');
      measureCtx.font = `${fontWeight} ${fontSize}px system-ui, sans-serif`;
      const dotGap = dotRadius * 2 + 4;
      const paddingX = 2;
      const width = dotGap + measureCtx.measureText(name).width + paddingX;
      const height = Math.max(fontSize + 6, dotRadius * 2 + 4);
      const canvas = document.createElement('canvas');
      canvas.width = width * scale;
      canvas.height = height * scale;
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);

      ctx.beginPath();
      ctx.arc(dotRadius + 1, height / 2, dotRadius, 0, Math.PI * 2);
      ctx.fillStyle = color;
      ctx.fill();
      ctx.strokeStyle = '#12161c';
      ctx.lineWidth = 1;
      ctx.stroke();

      ctx.font = `${fontWeight} ${fontSize}px system-ui, sans-serif`;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      // A dark halo behind the text keeps it legible over roads/other map content without a
      // solid background box.
      ctx.lineWidth = 3;
      ctx.strokeStyle = 'rgba(18,22,28,0.85)';
      ctx.strokeText(name, dotGap, height / 2 + 0.5);
      ctx.fillStyle = color;
      ctx.fillText(name, dotGap, height / 2 + 0.5);
      return { imageData: ctx.getImageData(0, 0, canvas.width, canvas.height), scale };
    }

    // State names: larger, letter-spaced, muted (atlas-style background context, not competing
    // with city names for attention), no position dot - a state covers a whole region, not a
    // point, so a marker there wouldn't mean anything the way it does for a city.
    function drawStateLabel(name) {
      const scale = 2;
      const fontSize = 15;
      const letterSpacing = 2;
      const upperName = name.toUpperCase();
      const measureCtx = document.createElement('canvas').getContext('2d');
      measureCtx.font = `600 ${fontSize}px system-ui, sans-serif`;
      let textWidth = 0;
      for (const ch of upperName) textWidth += measureCtx.measureText(ch).width + letterSpacing;
      const paddingX = 4;
      const width = textWidth + paddingX * 2;
      const height = fontSize + 6;
      const canvas = document.createElement('canvas');
      canvas.width = width * scale;
      canvas.height = height * scale;
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);
      ctx.font = `600 ${fontSize}px system-ui, sans-serif`;
      ctx.textBaseline = 'middle';
      ctx.lineWidth = 3;
      ctx.strokeStyle = 'rgba(18,22,28,0.7)';
      ctx.fillStyle = '#6b7686';
      let x = paddingX;
      for (const ch of upperName) {
        ctx.strokeText(ch, x, height / 2 + 0.5);
        ctx.fillText(ch, x, height / 2 + 0.5);
        x += measureCtx.measureText(ch).width + letterSpacing;
      }
      return { imageData: ctx.getImageData(0, 0, canvas.width, canvas.height), scale };
    }

    // Guards company-logo fetches only - the drawn-on-canvas ids above (city-label:, road-sign:,
    // etc.) are cheap synchronous draws with no fetch to de-dupe, and MapLibre itself only ever
    // fires styleimagemissing once per id in the normal case anyway. A real network fetch is slow
    // enough that two tiles referencing the same not-yet-loaded company could plausibly both miss
    // before the first addImage() call lands, which would otherwise mean two fetches for one logo.
    const pendingCompanyLogos = new Set();

    map.on('styleimagemissing', e => {
      const id = e.id;
      try {
        if (id.startsWith('city-label:')) {
          const rest = id.slice('city-label:'.length);
          const lastColon = rest.lastIndexOf(':');
          const name = rest.slice(0, lastColon);
          const scaleRank = Number(rest.slice(lastColon + 1)) || 0;
          const { imageData, scale } = drawCityLabel(name, scaleRank);
          map.addImage(id, imageData, { pixelRatio: scale });
        } else if (id.startsWith('town-label:')) {
          // Small settlements from highway mileage-sign data (see extract-town-labels.ts) - not
          // real ATS "cities", so no scaleRank of their own. Reusing drawCityLabel with a fixed
          // pseudo-rank past its own dimmest/smallest bracket keeps every town labeled (matching
          // the in-game world map's own density) while staying visually subordinate to every real
          // city - which is also why symbol-sort-key gives real cities collision priority over
          // these when space is tight.
          const name = id.slice('town-label:'.length);
          const { imageData, scale } = drawCityLabel(name, 11);
          map.addImage(id, imageData, { pixelRatio: scale });
        } else if (id.startsWith('state-label:')) {
          const name = id.slice('state-label:'.length);
          const { imageData, scale } = drawStateLabel(name);
          map.addImage(id, imageData, { pixelRatio: scale });
        } else if (id.startsWith('road-sign:')) {
          const [shieldType, number] = id.slice('road-sign:'.length).split(':');
          const { imageData, scale } = shieldType === 'euMotorway' || shieldType === 'euNational'
            ? drawEuShield(number, shieldType === 'euMotorway' ? 'motorway' : 'national')
            : drawShield(number, shieldType);
          map.addImage(id, imageData, { pixelRatio: scale });
        } else if (id.startsWith('company-logo:')) {
          // Real logo artwork (see Assets\WebMap\GENERATION.md - extracted once at build time from
          // the game's own material/ui/company/small/<token>.tobj), not something drawn - so this
          // fetches the bundled PNG instead of calling one of the drawXxx() canvas helpers above.
          const token = id.slice('company-logo:'.length);
          if (pendingCompanyLogos.has(token)) {
            return;
          }
          pendingCompanyLogos.add(token);
          fetch(`${window.location.origin}/company-logos/${token}.png`)
            .then(r => {
              if (!r.ok) throw new Error(`HTTP ${r.status}`);
              return r.blob();
            })
            .then(blob => createImageBitmap(blob))
            .then(bitmap => {
              if (!map.hasImage(id)) {
                map.addImage(id, bitmap);
              }
            })
            .catch(err => reportToHost('error', `company logo fetch failed for '${token}': ${err}`))
            .finally(() => pendingCompanyLogos.delete(token));
        }
      } catch (err) {
        reportToHost('error', `styleimagemissing failed for ${id}: ${err}`);
      }
    });

    map.on('error', e => reportToHost('error', `maplibre error: ${e.error ? e.error.message : JSON.stringify(e)}`));
    map.on('load', () => {
      reportToHost('info', 'map load event fired');

      const fuel = drawIconBadge('fuel', 20, '#3ecf6e', '#0c1f14');
      map.addImage('poi-fuel', fuel.imageData, { pixelRatio: fuel.scale });
      const weigh = drawBadge('W', 20, '#FFC24C', '#241a05');
      map.addImage('poi-weigh', weigh.imageData, { pixelRatio: weigh.scale });
      const rest = drawIconBadge('bed', 20, '#4C9AFF', '#08182b');
      map.addImage('poi-rest', rest.imageData, { pixelRatio: rest.scale });
      const service = drawIconBadge('wrench', 20, '#FF7043', '#2b0f05');
      map.addImage('poi-service', service.imageData, { pixelRatio: service.scale });

      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ type: 'mapReady' });
      }
    });

    map.addControl(new NavigationControl({ showCompass: false }), 'top-right');

    // Manual routing: click anywhere on the map to route from the truck's current position to that
    // point - drops a pin for visual confirmation and asks the C# side (GpsMapViewModel) to compute
    // the route, same pipeline as a job's own destination.
    function createPinElement() {
      const el = document.createElement('div');
      el.style.width = '20px';
      el.style.height = '26px';
      el.innerHTML = `
        <svg viewBox="0 0 20 26" width="20" height="26" style="display:block">
          <path d="M10 0 C4.5 0 0 4.5 0 10 C0 17.5 10 26 10 26 C10 26 20 17.5 20 10 C20 4.5 15.5 0 10 0 Z"
                fill="#5FD85F" stroke="#12161c" stroke-width="1.2" />
          <circle cx="10" cy="10" r="3.6" fill="#12161c" />
        </svg>`;
      return el;
    }

    // Placing a pin is opt-in (must press the Pin button first) rather than reacting to every map
    // click - a plain click used for panning/scrolling was registering as an accidental pin drop.
    map.on('click', e => {
      if (!pinModeActive) {
        return;
      }
      pinModeActive = false;
      pinBtn.classList.remove('active');
      map.getCanvas().style.cursor = '';

      const { lng, lat } = e.lngLat;
      if (!pinMarker) {
        pinMarker = new Marker({ element: createPinElement(), anchor: 'bottom' }).setLngLat([lng, lat]).addTo(map);
      } else {
        pinMarker.setLngLat([lng, lat]);
      }
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ type: 'setPinDestination', lon: lng, lat });
      }
    });

    function createMarkerElement() {
      // 25% larger than the original 22px (-> 27.5px) - the plain amber triangle was blending into
      // some roads at typical driving zoom, hard to spot at a glance.
      const el = document.createElement('div');
      el.style.width = '27.5px';
      el.style.height = '27.5px';
      el.innerHTML = `
        <svg viewBox="0 0 24 24" width="27.5" height="27.5" style="display:block">
          <path d="M12 2 L20 20 L12 15.5 L4 20 Z" fill="#FFC24C" stroke="#12161c" stroke-width="1.2" />
        </svg>`;
      return el;
    }

    // The route displayed here trims progressively as the truck drives (only ever shows what's
    // ahead, not behind) - this used to mean GpsMapViewModel pushing the entire remaining route
    // (up to ~3,500 points) through the WebView2 message bridge on nearly every ~1s position tick,
    // which got visibly sluggish once it was live for a whole drive: a fresh JSON serialization +
    // IPC round-trip + full MapLibre GeoJSON re-tessellation, once a second, for the whole trip.
    // The actual live-position push already happens every tick regardless (tiny payload, 4
    // numbers) - fullRoutePoints holds the last real route setRoute() received, and setLivePosition
    // trims its own local copy of it using that same already-arriving position data, so a full
    // route re-push over the bridge only happens on a genuine new route (job change, a manual pin,
    // a deviation reroute - all comparatively rare), not on every single tick of ordinary driving.
    let fullRoutePoints = null;

    function setLivePosition(pos) {
      lastPos = pos;
      if (!marker) {
        marker = new Marker({ element: createMarkerElement(), rotationAlignment: 'map' })
          .setLngLat([pos.lon, pos.lat])
          .addTo(map);
      } else {
        marker.setLngLat([pos.lon, pos.lat]);
      }
      marker.setRotation(pos.bearing);

      if (follow) {
        // An eased 600ms pan happening on essentially every position tick meant the map was mid-
        // animation - continuously repainting every frame for that whole window - almost the
        // entire time follow mode was on, not just occasionally. jumpTo repaints once, instantly,
        // for the same net camera movement (position updates roughly once a second anyway, so the
        // eased "smoothness" mostly wasn't visible over that gap regardless) - a real ongoing GPU
        // cost traded for a small loss of animation polish.
        map.jumpTo({ center: [pos.lon, pos.lat] });
      }

      trimRouteToPosition(pos);
    }

    // Finds the closest point on the currently-held route to `pos` and, if it's not the first
    // point anymore, drops everything before it and redraws - a plain lon/lat distance (not a real
    // geodesic one) is plenty for this, since it's only ever picking among points already a few
    // hundred meters apart along a route we already know is nearby, not measuring an exact
    // distance the way GpsMapViewModel's own off-route check (in real meters, server-side) does.
    function trimRouteToPosition(pos) {
      if (!fullRoutePoints || fullRoutePoints.length < 2) {
        return;
      }
      let nearestIndex = 0;
      let nearestDistSq = Infinity;
      for (let i = 0; i < fullRoutePoints.length; i++) {
        const dLon = fullRoutePoints[i][0] - pos.lon;
        const dLat = fullRoutePoints[i][1] - pos.lat;
        const distSq = dLon * dLon + dLat * dLat;
        if (distSq < nearestDistSq) {
          nearestDistSq = distSq;
          nearestIndex = i;
        }
      }
      if (nearestIndex > 0) {
        fullRoutePoints = fullRoutePoints.slice(nearestIndex);
        drawRoute(fullRoutePoints);
      }
    }

    // Backend-computed (see RoutingService.cs) - pushed on a genuine new route (job/pin/deviation
    // change), not an accumulating position history like the earlier "breadcrumb trail" that was
    // removed, and not on every trim tick either (see trimRouteToPosition above).
    function setRoute(points) {
      reportToHost('info', `setRoute called with ${points ? points.length : 0} points`);
      fullRoutePoints = points && points.length >= 2 ? points : null;
      drawRoute(points);
    }

    function drawRoute(points) {
      const routeSource = map.getSource('route');
      if (!routeSource) {
        reportToHost('error', 'drawRoute: route source not found on map');
        return;
      }
      // A LineString needs at least 2 points - an empty/cleared route (job delivered/cancelled)
      // clears the layer via an empty FeatureCollection instead of a degenerate geometry.
      if (!points || points.length < 2) {
        routeSource.setData({ type: 'FeatureCollection', features: [] });
        return;
      }
      routeSource.setData({
        type: 'Feature',
        geometry: { type: 'LineString', coordinates: points },
        properties: {},
      });
    }

    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener('message', event => {
        const msg = event.data;
        if (!msg || !msg.type) {
          return;
        }
        if (msg.type === 'livePosition') {
          setLivePosition(msg);
        } else if (msg.type === 'route') {
          setRoute(msg.points);
        } else if (msg.type === 'nextTurn') {
          lastTurn = msg.isRight == null ? null : { isRight: msg.isRight, distance: msg.distance };
          renderTurnSign();
        } else if (msg.type === 'lanStatus') {
          lanBtn.classList.toggle('active', msg.active);
          if (msg.active && msg.url) {
            lanStatus.textContent = `LAN: ${msg.url}`;
            lanStatus.classList.add('visible');
          } else {
            lanStatus.classList.remove('visible');
          }
        } else if (msg.type === 'fpsCap') {
          // See the requestAnimationFrame throttle near the top of this file - CoreBalancer (the
          // same per-core CPU sampling that already steers this map's own renderer/GPU processes
          // off the busiest cores) decides whether there's real system headroom to allow 60fps, or
          // whether 30fps is the safer default given what else is running (the game, chiefly).
          window.__mapTargetFps = msg.targetFps;
          reportToHost('info', `map FPS cap set to ${msg.targetFps}`);
        }
      });
    } else {
      // Remote LAN view: no postMessage bridge, so poll GpsLanServer's read-only JSON endpoints
      // instead. Position moves continuously so it's polled fast (matches GpsMapViewModel's own
      // ~1s push rate on the local side); the route only ever changes on an actual destination
      // change, so a much slower poll is plenty and avoids hammering the server for nothing.
      const poll = (url, onData) => {
        fetch(url)
          .then(r => r.json())
          .then(onData)
          .catch(err => reportToHost('error', `poll ${url} failed: ${err}`));
      };
      poll('/api/position', pos => { if (pos) setLivePosition(pos); });
      poll('/api/route', data => setRoute(data.points));
      setInterval(() => poll('/api/position', pos => { if (pos) setLivePosition(pos); }), 1500);
      setInterval(() => poll('/api/route', data => setRoute(data.points)), 5000);
    }
  })
  .catch(err => reportToHost('error', `failed to load maplibre module: ${err}`));
