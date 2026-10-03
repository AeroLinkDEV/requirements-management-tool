import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import type { AircraftData, RoutePoint } from "./efis";
import { constraintAltitude } from "./flight";
import {
  AIRCRAFT_PARTS, FT, MESH_MAX_ZOOM, RELIEF_MAX_ZOOM, TILE_PIXELS, ancestorOf, blendAircraft, cameraPose, pixelMetres,
  routeHeights, sampleHeights, shadeTile, tileLatitude, type AircraftSample, type Layout, type View,
} from "./outTheWindow";
import { GroundImagery, IMAGERY_MAX_ZOOM, browserImageryDecoder, type ImagerySource } from "./groundImagery";
import { MAIN_ROTOR_RAD_S, createAircraftModel } from "./otwAircraftModel";
import { createObstacleLayer } from "./otwObstacles";
import { workerReliefShader } from "./reliefShader";
import { aircraftCamera } from "./otwCamera";
import { aircraftRenderLoop } from "./otwRenderLoop";
import { useFmsStationDocument } from "./FmsStationSurface";
import {
  ABSOLUTE_BANDS_FT, ABSOLUTE_RGB, CAUTION_RGB, DANGER_RGB, RELATIVE_CAUTION_FT, RELATIVE_DANGER_FT, type TerrainColouring,
} from "./terrainAwareness";
import type { TerrainTiles } from "./terrainTiles";
import "./FmsOutTheWindow.css";

/** The modes on the flight mode annunciator, as the bench's Flight card shows them. */
export type HudModes = { lateral: string; vertical: string; armed: string[]; angleReference?: "MAG" | "TRUE"; magneticVariation?: number };

/** What the ground shows: aerial imagery where there is some (the United States), relief elsewhere; or relief only. */
export type Ground = "imagery" | "relief";

type Props = {
  air: AircraftData; route: RoutePoint[]; modes: HudModes; layout: Layout; view: View; tiles: TerrainTiles;
  ground: Ground; colouring: TerrainColouring; imagery: GroundImagery<ImageBitmap>;
  controls: ReactNode;
};

/** The imagery tiles for a bench, from a source (the server's relay, or a test fixture's), decoded by the browser. */
export const groundImagery = (source: ImagerySource) => new GroundImagery(source, browserImageryDecoder());

type Status = "loading" | "ready" | "no-webgl" | "failed";
type SceneProgress = { firstFrame: boolean; groundSettled: boolean; model: "loading" | "glb" | "fallback" };
const initialProgress: SceneProgress = { firstFrame: false, groundSettled: false, model: "loading" };

/** Heights per side of a terrain mesh tile: 65 puts a vertex about every 75 m at level 13, where 33 put one every 150 m. */
const MESH_SAMPLES = 65;
const ROUTE_MAGENTA = "#e04cd6";

// Cesium fetches its workers and assets at run time from here: the development server answers it from the package,
// and a build copies them beside the bundle (vite.config.ts), so they come from this server either way (DEC-047).
const CESIUM_BASE = `${import.meta.env.BASE_URL}cesium/`;

type Live = { from: AircraftSample; to: AircraftSample; at: number; interval: number; view: View; layout: Layout };
type SceneHandle = {
  setRoute: (route: RoutePoint[], altitude: number) => void; setGround: (ground: Ground) => void; setColouring: (colouring: TerrainColouring) => void;
  requestRender: () => void; destroy: () => void;
};

/**
 * The view out of the aircraft, drawn with CesiumJS over open elevation data: the ground coloured by height and
 * shaded by slope, the way a synthetic vision system draws it, with the active route in magenta. The camera follows
 * the simulated aircraft (efis.ts `aircraftData`), so it shows what the flight model is doing, not a separate game.
 *
 * Two layouts: HUD puts the primary flight data over a large window; Panel is a shorter window over a glareshield, as a
 * seated pilot sees it, with the bench's CDU, PFD and ND below it standing in for the instrument panel. The engine is
 * loaded only when this is first shown.
 */
export default function FmsOutTheWindow({ air, route, modes, layout, view, tiles, ground, colouring, imagery, controls }: Props) {
  const renderingDocument = useFmsStationDocument();
  const details = useRef<HTMLDialogElement>(null);
  const detailsButton = useRef<HTMLButtonElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const credits = useRef<HTMLDivElement>(null);
  const creditViewport = useRef<HTMLDivElement>(null);
  const pathMarker = useRef<HTMLDivElement>(null);
  const scene = useRef<SceneHandle | null>(null);
  const [sceneEpoch, setSceneEpoch] = useState(0);
  const [status, setStatus] = useState<Status>("loading");
  const [failure, setFailure] = useState("");
  const [progress, setProgress] = useState({ ...initialProgress, destination: renderingDocument, tiles, imagery });
  const terrain = useSyncExternalStore(listener => tiles.subscribe(listener), () => tiles.status);
  const imageryStatus = useSyncExternalStore(listener => imagery.subscribe(listener), () => imagery.status);
  const esriImagery = useSyncExternalStore(listener => imagery.subscribe(listener), () => imagery.usesEsri);

  useLayoutEffect(() => {
    const dialog = details.current!;
    const opener = detailsButton.current!;
    return () => {
      // Native modal close restores its opener. During transfer/unmount, clear only that unintended
      // restoration in the old document; the station lifecycle restores its own focus afterward.
      const focused = renderingDocument.activeElement;
      if (dialog.open) dialog.close();
      if (opener.isConnected && opener.ownerDocument === renderingDocument
        && renderingDocument.activeElement === opener && focused !== opener) {
        opener.blur();
        if (focused?.isConnected && focused.ownerDocument === renderingDocument && !dialog.contains(focused)
          && focused !== renderingDocument.body && focused !== renderingDocument.documentElement) (focused as HTMLElement).focus();
      }
    };
  }, [renderingDocument]);
  const openDetails = () => {
    const button = detailsButton.current, dialog = details.current;
    if (!button?.isConnected || !dialog?.isConnected || button.ownerDocument !== renderingDocument || dialog.ownerDocument !== renderingDocument) return;
    // Pointer activation does not focus buttons in every browser. Establish the actual modal opener.
    button.focus();
    dialog.showModal();
  };
  const closeDetails = () => {
    details.current?.close();
    const button = detailsButton.current;
    if (button?.isConnected && button.ownerDocument === renderingDocument) button.focus();
  };

  // The scene reads this every frame; renders only move its target.
  const live = useRef<Live | null>(null);
  useLayoutEffect(() => {
    // The helicopter profile's hover-data flag, where the aircraft data carries one.
    const hoverData = (air as AircraftData & { helicopter?: { hoverData?: boolean } | null }).helicopter?.hoverData === true;
    const sample: AircraftSample = { position: air.position, altitude: air.physicalAltitude, heading: air.heading, pitch: air.pitch, bank: air.bank, hoverData };
    const previous = live.current, now = performance.now();
    if (!previous) { live.current = { from: sample, to: sample, at: now, interval: 250, view, layout }; return; }
    if (!sameSample(previous.to, sample)) {
      // The drawn position so far becomes the start of the next blend, so a tick arriving early or late never jumps.
      const drawnAt = blendAircraft(previous.from, previous.to, (now - previous.at) / previous.interval);
      live.current = { from: drawnAt, to: sample, at: now, interval: Math.min(1000, Math.max(50, now - previous.at)), view, layout };
    } else if (previous.view !== view || previous.layout !== layout) {
      live.current = { ...previous, view, layout };
    } else return;
    // The scene draws only when something changes (render on request): a new tick, view or layout is a change.
    scene.current?.requestRender();
  });

  useEffect(() => {
    let disposed = false;
    let handle: SceneHandle | null = null;
    // The host survives docking, but its diagnostics must describe this scene generation, including startup.
    for (const name of ["frames", "frameCauses", "requests", "tileQueue", "tilesLoaded", "model", "obstacles"]) delete host.current!.dataset[name];
    setStatus("loading");
    setFailure("");
    setProgress({ ...initialProgress, destination: renderingDocument, tiles, imagery });
    const failed = (error: unknown) => {
      if (disposed) return;
      setFailure(error instanceof Error ? error.message : String(error));
      setStatus(/webgl/i.test(String(error)) ? "no-webgl" : "failed");
    };
    startScene(host.current!, credits.current!, creditViewport.current!, pathMarker.current!, live, tiles, imagery, renderingDocument, () => disposed, failed,
      next => { if (!disposed) setProgress({ ...next, destination: renderingDocument, tiles, imagery }); })
      .then(created => {
        if (disposed) { created.destroy(); return; }
        handle = created;
        scene.current = created;
        // A cached engine import can coalesce loading/ready renders during document transfer. Each new
        // handle must receive the current ground/colouring/route even when those props never changed.
        setSceneEpoch(value => value + 1);
        setStatus("ready");
      }, failed);
    return () => { disposed = true; handle?.destroy(); scene.current = null; };
  }, [tiles, imagery, renderingDocument]);

  useEffect(() => { scene.current?.setGround(ground); }, [ground, sceneEpoch]);
  useEffect(() => { scene.current?.setColouring(colouring); }, [colouring, sceneEpoch]);

  const routeKey = route.map(point => `${point.ident}:${point.position.lat},${point.position.lon}:${point.constraint ?? ""}:${point.active}`).join("|");
  useEffect(() => {
    scene.current?.setRoute(route, air.physicalAltitude);
  }, [routeKey, sceneEpoch]); // eslint-disable-line react-hooks/exhaustive-deps

  const hud = layout === "hud" && view === "cockpit";
  // Engine construction, a public draw and tile settlement are separate stages. None establishes
  // compositor presentation or completed GPU lighting. A destination change starts a new scene.
  const currentProgress = progress.destination === renderingDocument && progress.tiles === tiles && progress.imagery === imagery ? progress : initialProgress;
  const pending = status === "loading" ? "Loading the 3D view…"
    : status !== "ready" ? null
    : [
      !currentProgress.firstFrame ? "Drawing the 3D scene…" : null,
      view === "chase" && currentProgress.model === "loading" ? "Loading the aircraft model…"
        : view === "chase" && currentProgress.model === "fallback" ? "Using a simplified aircraft." : null,
      currentProgress.firstFrame && !currentProgress.groundSettled ? "Loading ground detail…" : null,
    ].filter(Boolean).join(" ");
  const fatal = status === "no-webgl" ? "This browser cannot draw 3D graphics (WebGL is unavailable), so the view is off."
    : status === "failed" ? `The 3D view could not start: ${failure}`
    : null;
  const terrainNote = terrain === "off" ? "Terrain data is off on this installation, so the ground is drawn flat. An administrator can turn it on with the FmsBench:TerrainRelay setting (the server then fetches open elevation tiles from AWS)."
    : terrain === "unreachable" ? "The server cannot reach the terrain source, so the ground is drawn flat where tiles are missing."
    : terrain === "waiting" ? "Waiting for terrain data." : "Terrain data is available.";
  const imageryNote = imageryStatus === "off" ? "Imagery is off on this installation, so the ground is drawn as relief. An administrator can turn it on with the FmsBench:ImageryRelay setting (the server then fetches USGS aerial imagery)."
    : imageryStatus === "unreachable" ? "The server cannot reach the imagery source, so the ground is drawn as relief where imagery is missing."
    : imageryStatus === "waiting" ? "Waiting for imagery data." : "Imagery data is available where the source has coverage.";
  const sourceWarning = terrain === "off" ? "Terrain off"
    : terrain === "unreachable" ? "Terrain missing"
    : ground === "imagery" && imageryStatus === "off" ? "Imagery off"
    : ground === "imagery" && imageryStatus === "unreachable" ? "Imagery missing"
    : null;

  return (<>
    <FmsOutTheWindowHeader controls={controls} notice={
      <button type="button" className="fmsOtwDetailsButton" ref={detailsButton} aria-label="View status and sources"
        aria-haspopup="dialog" onClick={openDetails} title="View status and sources">ⓘ</button>
    } status={!fatal && (pending || sourceWarning) ? <p className="fmsOtwHeaderStatus" role="status" aria-label="Out-the-window view status">
      {sourceWarning ? <><span className="fmsOtwSourceWarning">{sourceWarning}</span>{pending ? " · " : null}</> : null}
      {pending}
    </p> : null} />
    <div className={`fmsOtw layout-${layout} view-${view}`} data-status={status} data-terrain={terrain} data-imagery={imageryStatus}
      data-ground={ground} data-colouring={colouring}>
      <div className="fmsOtwScene" ref={host} />
      {layout === "panel" && view === "cockpit" ? <div className="fmsOtwGlareshield" aria-hidden="true" /> : null}
      <div className="fmsOtwPathMarker" ref={pathMarker} hidden={!hud || status !== "ready"} aria-hidden="true">
        <svg viewBox="-40 -14 80 28"><circle r="7" /><path d="M-7 0 H-30 M7 0 H30 M0 -7 V-14" /></svg>
      </div>
      {hud ? <Hud air={air} modes={modes} /> : null}
      {fatal ? <p className="fmsOtwNote" role="status">{fatal}</p> : null}
    </div>
    <div className="fmsOtwCredits">
      <div ref={credits} />
      <span>
        Terrain: Mapzen Terrain Tiles on AWS Open Data (SRTM, GMTED2010, USGS NED and others).
        {ground === "imagery" ? " Imagery: USGS The National Map, USDA NAIP (public domain)." : null}
        {ground === "imagery" && esriImagery ? " Imagery outside the United States: Esri, Maxar, Earthstar Geographics, and the GIS User Community. Powered by Esri." : null}
        {" "}Route fixes are invented.
      </span>
    </div>
    <div className="fmsOtwCreditViewport" ref={creditViewport}
      onKeyDown={event => { if (event.key === "Escape") event.stopPropagation(); }} />
    <dialog className="fmsOtwDetails" ref={details} aria-label="View status and sources"
      onKeyDown={event => { if (event.key === "Escape") event.stopPropagation(); }}
      onCancel={event => { event.preventDefault(); event.stopPropagation(); closeDetails(); }}>
      <h2>View status and sources</h2>
      <p>{fatal || pending || "The 3D view is ready."}</p>
      <h3>Terrain</h3><p>{terrainNote}</p>
      <h3>Imagery</h3><p>{ground === "relief" ? "Relief is selected; imagery is not drawn. " : null}{imageryNote}</p>
      <button type="button" onClick={closeDetails}>Close</button>
    </dialog>
  </>);
}

/** The shown and hidden view use the same toolbar; status remains with the shown scene owner. */
export function FmsOutTheWindowHeader({ controls, notice, status }: { controls: ReactNode; notice?: ReactNode; status?: ReactNode }) {
  return <div className="fmsBenchMapHead">
    <div className="fmsOtwHeaderTitleGroup">
      <h2 className="fmsOtwHeaderTitle">Out the window</h2>
      {notice ? <span className="fmsOtwHeaderNotice">{notice}</span> : null}
      {status}
    </div>
    {controls}
  </div>;
}

const sameSample = (a: AircraftSample, b: AircraftSample) =>
  a.position.lat === b.position.lat && a.position.lon === b.position.lon && a.altitude === b.altitude && a.heading === b.heading && a.bank === b.bank && a.pitch === b.pitch && a.hoverData === b.hoverData;

const three = (degrees: number) => String(((Math.round(degrees) % 360) + 360) % 360 || 360).padStart(3, "0");

/** The head-up symbology: modes, speed, altitude, heading and bank. The flight path marker moves every frame, in the scene. */
function Hud({ air, modes }: { air: AircraftData; modes: HudModes }) {
  const ticks = [];
  const angularAvailable = modes.angleReference !== "MAG" || typeof modes.magneticVariation === "number" && Number.isFinite(modes.magneticVariation);
  const heading = air.heading - (modes.angleReference === "MAG" ? modes.magneticVariation ?? 0 : 0);
  for (let offset = -30; offset <= 30; offset += 5) {
    const value = Math.round(heading / 5) * 5 + offset, x = ((((value - heading) % 360) + 540) % 360 - 180) * 4;
    ticks.push(<line key={offset} x1={x} x2={x} y1={0} y2={value % 10 === 0 ? 10 : 6} />);
  }
  return (
    <div className="fmsOtwHud" aria-label="Head-up display">
      <div className="fmsOtwFma" role="status" aria-label="Head-up flight modes">
        <span>{modes.lateral}</span><span>{modes.vertical}</span>
        {modes.armed.length ? <span className="armed">{modes.armed.join(" ")}</span> : null}
      </div>
      <svg className="fmsOtwBank" viewBox="-70 -8 140 44" aria-hidden="true">
        <path d="M-60 30 A 70 70 0 0 1 60 30" />
        {[-30, -20, -10, 0, 10, 20, 30].map(angle => (
          <line key={angle} x1={0} y1={-2} x2={0} y2={angle % 30 === 0 ? 8 : 5} transform={`rotate(${angle} 0 68)`} />
        ))}
        <path className="pointer" d="M0 10 l-5 8 h10 z" transform={`rotate(${-air.bank} 0 68)`} />
      </svg>
      <div className="fmsOtwTape speed"><small>IAS</small><strong>{Math.round(air.airspeed)}</strong><small>GS {Math.round(air.groundSpeed)}</small></div>
      <div className="fmsOtwTape altitude"><small>ALT</small><strong>{Math.round(air.altitude / 10) * 10}</strong><small>VS {Math.round(air.verticalSpeed / 50) * 50}</small></div>
      <div className="fmsOtwHeading">
        <svg viewBox="-120 0 240 12" preserveAspectRatio="none" aria-hidden="true">{angularAvailable ? ticks : null}</svg>
        <strong>{angularAvailable ? three(heading) : "---"}{modes.angleReference === "MAG" ? "°" : "T"}</strong>
      </div>
    </div>
  );
}

/** A 256-pixel canvas; the ground imagery, the terrain decode and the ownship symbol all draw on one. */
const canvas = (owner: Document, size = TILE_PIXELS) => Object.assign(owner.createElement("canvas"), { width: size, height: size });

async function startScene(
  container: HTMLElement, creditContainer: HTMLElement, creditViewport: HTMLElement, pathMarker: HTMLElement, live: { current: Live | null },
  tiles: TerrainTiles, imagery: GroundImagery<ImageBitmap>,
  renderingDocument: Document, disposed: () => boolean, onFailure: (error: unknown) => void,
  onProgress: (progress: SceneProgress) => void,
): Promise<SceneHandle> {
  (window as unknown as { CESIUM_BASE_URL: string }).CESIUM_BASE_URL = CESIUM_BASE;
  // The engine alone: the `cesium` package's widgets evaluate a string as script, which the policy refuses.
  const Cesium = await import("@cesium/engine");
  // An import can complete after Return/close. Never create an old destination's widget in the adopted host.
  if (disposed() || container.ownerDocument !== renderingDocument) throw new Error("The 3D view destination changed before startup completed.");
  const renderingWindow = renderingDocument.defaultView;
  if (!renderingWindow || renderingWindow.closed) throw new Error("The 3D view's window is closed.");

  // Each height tile is fetched once (terrainTiles.ts), however many mesh and imagery tiles are cut from it.
  const heights = (z: number, x: number, y: number) => tiles.load(z, x, y);

  const tilingScheme = new Cesium.WebMercatorTilingScheme();
  const terrainProvider = new Cesium.CustomHeightmapTerrainProvider({
    width: MESH_SAMPLES, height: MESH_SAMPLES, tilingScheme,
    callback: async (x, y, level) => {
      const source = ancestorOf(level, x, y, MESH_MAX_ZOOM);
      const tile = await heights(source.z, source.x, source.y);
      return tile ? sampleHeights(tile, source.offsetX, source.offsetY, source.span, MESH_SAMPLES) : new Float32Array(MESH_SAMPLES * MESH_SAMPLES);
    },
  });
  // A disabled relay has no heights to refine. Cesium's native flat provider uses smaller zero-height
  // meshes on the same tile grid; live/missing/unreachable terrain keeps the measured-height provider.
  const flatTerrainProvider = new Cesium.EllipsoidTerrainProvider({ tilingScheme });

  // The ground: aerial imagery where the relay has some (groundImagery.ts: the United States), otherwise relief drawn
  // from the same heights, shaded in a worker (reliefShader.ts) so a burst of new tiles does not freeze the page.
  // Imagery goes deeper than relief (16 against 14); past the relief's depth, a tile with no imagery is refused, and
  // Cesium draws its parent's relief there instead.
  const shader = workerReliefShader();
  const flatRelief = canvas(renderingDocument);
  flatRelief.getContext("2d")!.putImageData(new ImageData(shadeTile(new Float32Array(TILE_PIXELS * TILE_PIXELS), 30), TILE_PIXELS, TILE_PIXELS), 0, 0);
  const reliefImage = async (x: number, y: number, level: number) => {
    const tile = await heights(level, x, y);
    if (!tile) return flatRelief;
    const rgba = await shader.shade(tile, pixelMetres(level, tileLatitude(level, y)));
    const image = canvas(renderingDocument);
    image.getContext("2d")!.putImageData(new ImageData(rgba, TILE_PIXELS, TILE_PIXELS), 0, 0);
    return image;
  };
  // Under a partial imagery tile (the edge of the coverage, transparent beyond it): its relief, or past the relief's
  // depth the part of its ancestor's relief that it covers. The layer is drawn opaque, so without this the far side of
  // a coast or of the border would be black.
  const underRelief = async (photo: ImageBitmap, x: number, y: number, level: number) => {
    const source = ancestorOf(level, x, y, RELIEF_MAX_ZOOM);
    const relief = await reliefImage(source.x, source.y, source.z);
    const image = canvas(renderingDocument);
    const context = image.getContext("2d")!;
    const side = source.span * TILE_PIXELS;
    context.drawImage(relief, source.offsetX * TILE_PIXELS, source.offsetY * TILE_PIXELS, side, side, 0, 0, TILE_PIXELS, TILE_PIXELS);
    context.drawImage(photo, 0, 0);
    photo.close();
    return image;
  };
  const groundProvider = (ground: Ground) => {
    // Confirmed off is sticky and its relief is the same opaque image everywhere. One world tile
    // preserves every pixel while avoiding a separate texture upload for each refined terrain mesh.
    const flat = ground === "relief" && tiles.status === "off";
    const errorEvent = new Cesium.Event();
    // A refused tile is expected (no imagery past the relief's depth): Cesium draws the parent, and nothing is retried.
    errorEvent.addEventListener((error: { retry: boolean }) => { error.retry = false; });
    return {
      tilingScheme, rectangle: tilingScheme.rectangle, tileWidth: TILE_PIXELS, tileHeight: TILE_PIXELS,
      minimumLevel: 0, maximumLevel: flat ? 0 : ground === "imagery" ? IMAGERY_MAX_ZOOM : RELIEF_MAX_ZOOM, hasAlphaChannel: false, ready: true,
      errorEvent, credit: undefined, proxy: undefined, tileDiscardPolicy: undefined,
      getTileCredits: () => [],
      pickFeatures: () => undefined,
      requestImage: async (x: number, y: number, level: number) => {
        if (flat) return flatRelief;
        if (ground === "imagery") {
          const photo = await imagery.load(level, x, y);
          if (photo) return photo.partial ? underRelief(photo.image, x, y, level) : photo.image;
        }
        if (level > RELIEF_MAX_ZOOM) throw new Error("no imagery here: the parent tile's relief is drawn");
        return reliefImage(x, y, level);
      },
    } as unknown as InstanceType<typeof Cesium.UrlTemplateImageryProvider>;
  };

  // Throws when the browser has no WebGL; the component says so rather than failing the page.
  let widget: InstanceType<typeof Cesium.CesiumWidget>;
  try { widget = new Cesium.CesiumWidget(container, {
    baseLayer: false, terrainProvider: tiles.status === "off" ? flatTerrainProvider : terrainProvider, creditContainer, creditViewport,
    skyBox: false, showRenderLoopErrors: false, targetFrameRate: 30, useBrowserRecommendedResolution: true, msaaSamples: 4,
    // Cesium's default loop closes over the importing owner's global RAF. A portal does not change that realm.
    useDefaultRenderLoop: false,
    // Draw only when something changes: a paused bench, or a view waiting for the next tick, costs nothing. Cesium
    // itself asks for frames while tiles load and when the window is resized.
    requestRenderMode: true, maximumRenderTimeChange: Number.POSITIVE_INFINITY,
  }); } catch (error) { shader.dispose(); throw error; }
  const { scene, camera } = widget;
  let progress = initialProgress;
  // Called only on actual public stage transitions, never dispatched to React on every frame.
  const progressed = (firstFrame: boolean, groundSettled: boolean, model = progress.model) => {
    if (firstFrame === progress.firstFrame && groundSettled === progress.groundSettled && model === progress.model) return;
    progress = { firstFrame, groundSettled, model };
    if (!disposed()) onProgress(progress);
  };
  // #1298: who asked for each frame, on the scene element, so a paused view that goes on drawing says whether this code
  // asked (and which part of it) or Cesium itself did, with Cesium's tile-load queue (it draws as tiles arrive).
  const requested: Record<string, number> = {};
  let asked = false;
  const request = (why: string) => {
    requested[why] = (requested[why] ?? 0) + 1;
    container.dataset.requests = Object.entries(requested).map(([source, count]) => `${source} ${count}`).join(", ");
    asked = true;
    scene.requestRender();
  };
  // #1298: why each frame was drawn, by the first that applies: this code asked, the camera had moved, the globe was
  // still loading tiles, or Cesium's own after-render work since the last frame: a web worker's task or a network
  // request completing (each asks for a frame), the texture atlas still filling, an event, or other. Counted per drawn
  // frame on the scene element, so a paused view that keeps drawing says which. Diagnostic only: it changes no frame.
  const internal = new Set<string>();
  const afterRender = (scene as unknown as { frameState: { afterRender: (() => unknown)[] } }).frameState.afterRender;
  const pushAfterRender = afterRender.push.bind(afterRender);
  afterRender.push = (...work: (() => unknown)[]) => {
    for (const task of work) {
      const text = String(task);
      internal.add(/textureAtlas/.test(text) ? "atlas" : /requestRender/.test(text) ? (/\bWorker\./.test(new Error().stack ?? "") ? "worker" : "request")
        : /raiseEvent/.test(text) ? "event" : "other");
    }
    return pushAfterRender(...work);
  };
  let tileQueue = 0;
  const stopQueueWatch = scene.globe.tileLoadProgressEvent.addEventListener((queued: number) => {
    container.dataset.tileQueue = String(queued);
    tileQueue = queued;
    progressed(progress.firstFrame, scene.globe.tilesLoaded && queued === 0);
  });
  scene.globe.depthTestAgainstTerrain = true;
  scene.globe.baseColor = Cesium.Color.fromBytes(74, 112, 62);
  scene.globe.maximumScreenSpaceError = 1.6;
  scene.fog.enabled = true;
  scene.fog.density = 1.4e-4;
  scene.screenSpaceCameraController.enableInputs = false;

  // The ground layer, replaced when the ground choice changes (imagery or relief).
  let groundChoice: Ground | null = null;
  let groundFlat = false;
  let groundLayer: InstanceType<typeof Cesium.ImageryLayer> | null = null;
  const setGround = (ground: Ground) => {
    const flat = ground === "relief" && tiles.status === "off";
    if (ground === groundChoice && flat === groundFlat) return;
    groundChoice = ground;
    groundFlat = flat;
    if (groundLayer) scene.imageryLayers.remove(groundLayer, true);
    groundLayer = scene.imageryLayers.addImageryProvider(groundProvider(ground), 0);
    request("ground");
  };
  setGround("imagery");
  const selectFlatTerrainWhenOff = () => {
    if (tiles.status !== "off") return;
    // One sticky-off transition rebuilds the mesh quadtree. Relief also becomes one uniform world
    // tile; photographic imagery and the terrain-colouring material retain their existing behavior.
    if (scene.globe.terrainProvider !== flatTerrainProvider) {
      scene.globe.terrainProvider = flatTerrainProvider;
      request("terrain off");
    }
    if (groundChoice === "relief") setGround("relief");
  };
  const stopTerrainWatch = tiles.subscribe(selectFlatTerrainWhenOff);
  selectFlatTerrainWhenOff();

  // Terrain colouring (terrainAwareness.ts), computed per pixel on the GPU from the height of the ground there: red and
  // amber against the aircraft's altitude (relative), or height bands (absolute), with a faint contour every 500 ft,
  // blended over the ground. It costs the page nothing: the aircraft's height is one number set each frame.
  const colour = (rgb: readonly number[], alpha: number) => new Cesium.Color(rgb[0] / 255, rgb[1] / 255, rgb[2] / 255, alpha);
  const CONTOUR = "float contour(float h) { float f = fract(h / 152.4); return (1.0 - smoothstep(0.0, 0.03, min(f, 1.0 - f))) * 0.35; }\n";
  const relative = new Cesium.Material({
    translucent: true,
    fabric: {
      type: "FmsRelativeTerrain",
      uniforms: {
        aircraft: 0.0, danger: RELATIVE_DANGER_FT * FT, caution: RELATIVE_CAUTION_FT * FT,
        dangerColour: colour(DANGER_RGB, 0.55), cautionColour: colour(CAUTION_RGB, 0.45),
      },
      source: CONTOUR + `czm_material czm_getMaterial(czm_materialInput materialInput) {
  czm_material m = czm_getDefaultMaterial(materialInput);
  float h = materialInput.height;
  vec4 c = h >= aircraft - danger ? dangerColour : h >= aircraft - caution ? cautionColour : vec4(0.0);
  c = mix(c, vec4(1.0, 1.0, 1.0, max(c.a, 0.35)), contour(h));
  m.diffuse = c.rgb; m.alpha = c.a;
  return m;
}`,
    },
  });
  const edges = ABSOLUTE_BANDS_FT.map(feet => feet * FT);
  const absolute = new Cesium.Material({
    translucent: true,
    fabric: {
      type: "FmsAbsoluteTerrain",
      uniforms: {
        edge0: edges[0], edge1: edges[1], edge2: edges[2], edge3: edges[3],
        band0: colour(ABSOLUTE_RGB[0], 0.3), band1: colour(ABSOLUTE_RGB[1], 0.3), band2: colour(ABSOLUTE_RGB[2], 0.3),
        band3: colour(ABSOLUTE_RGB[3], 0.3), band4: colour(ABSOLUTE_RGB[4], 0.3),
      },
      source: CONTOUR + `czm_material czm_getMaterial(czm_materialInput materialInput) {
  czm_material m = czm_getDefaultMaterial(materialInput);
  float h = materialInput.height;
  vec4 c = h >= edge3 ? band4 : h >= edge2 ? band3 : h >= edge1 ? band2 : h >= edge0 ? band1 : band0;
  c = mix(c, vec4(1.0, 1.0, 1.0, max(c.a, 0.35)), contour(h));
  m.diffuse = c.rgb; m.alpha = c.a;
  return m;
}`,
    },
  });
  let colouringChoice: TerrainColouring = "off";
  const setColouring = (colouring: TerrainColouring) => {
    colouringChoice = colouring;
    scene.globe.material = colouring === "relative" ? relative : colouring === "absolute" ? absolute : undefined;
    request("colouring");
  };

  const routeLine = scene.primitives.add(new Cesium.PolylineCollection());
  const fixes = scene.primitives.add(new Cesium.PointPrimitiveCollection());
  const labels = scene.primitives.add(new Cesium.LabelCollection());
  const magenta = Cesium.Color.fromCssColorString(ROUTE_MAGENTA);

  // The aircraft seen from outside. Behind it (chase), a helicopter from boxes and ellipsoids (outTheWindow.ts), placed
  // and oriented every frame: the opaque body and the translucent rotor discs are two primitives, since translucency
  // is a property of a primitive's appearance. From 30,000 ft above (map), where the model would be a dot, a plan-view
  // symbol.
  const vertexFormat = Cesium.PerInstanceColorAppearance.VERTEX_FORMAT;
  const modelPart = (translucent: boolean) => scene.primitives.add(new Cesium.Primitive({
    geometryInstances: AIRCRAFT_PARTS.filter(part => (part.alpha !== undefined) === translucent).map(part => new Cesium.GeometryInstance({
      geometry: part.shape === "box"
        ? Cesium.BoxGeometry.fromDimensions({ dimensions: new Cesium.Cartesian3(...part.size), vertexFormat })
        : new Cesium.EllipsoidGeometry({ radii: new Cesium.Cartesian3(...part.size), vertexFormat }),
      modelMatrix: Cesium.Matrix4.fromTranslation(new Cesium.Cartesian3(...part.offset)),
      attributes: { color: Cesium.ColorGeometryInstanceAttribute.fromColor(Cesium.Color.fromBytes(...part.colour, Math.round((part.alpha ?? 1) * 255))) },
      id: part.name,
    })),
    appearance: new Cesium.PerInstanceColorAppearance({ closed: true, translucent }),
    asynchronous: false,
    show: false,
  }));
  const models = [modelPart(false), modelPart(true)];
  // The glTF helicopter (otwAircraftModel.ts), with turning rotors; the boxes and ellipsoids stay the fallback until it
  // has loaded, or if it cannot. The scene draws only on change, so it asks for a frame once the model is there.
  const helicopter = createAircraftModel(Cesium, { primitives: scene.primitives, requestRender: () => request("model") });
  void helicopter.ready.then(outcome => {
    if (disposed()) return;
    const model = "loaded" in outcome ? "glb" : "fallback";
    container.dataset.model = model;
    progressed(progress.firstFrame, progress.groundSettled, model);
    request("model");
  });
  // The FAA obstacles near the bench areas (otwObstacles.ts, Brief C), coloured as the terrain colouring colours their
  // tops; drawn once the extract has loaded, which the scene element records (the count, or "failed").
  const obstacles = createObstacleLayer(Cesium, { primitives: scene.primitives, postRender: scene.postRender,
    renderError: scene.renderError, requestRender: () => request("obstacles") });
  void obstacles.ready.then(outcome => { if (disposed()) return; container.dataset.obstacles = "drawn" in outcome ? String(outcome.drawn) : "failed"; request("obstacles"); });
  const orientation = new Cesium.HeadingPitchRoll();
  // The plan-view symbol: the rotor disc, the fuselage and the tail boom, nose up.
  const symbol = canvas(renderingDocument, 48);
  const pen = symbol.getContext("2d")!;
  pen.translate(24, 22);
  pen.fillStyle = "#3ddc84"; pen.strokeStyle = "#05080b"; pen.lineWidth = 1.5;
  pen.beginPath(); pen.arc(0, 0, 17, 0, 2 * Math.PI); pen.globalAlpha = 0.35; pen.fill(); pen.globalAlpha = 1; pen.stroke();
  pen.beginPath(); pen.ellipse(0, -1, 4.5, 8, 0, 0, 2 * Math.PI); pen.fill(); pen.stroke();
  pen.beginPath(); pen.rect(-1.2, 6, 2.4, 14); pen.fill(); pen.stroke();
  pen.beginPath(); pen.rect(-5, 18, 10, 2.5); pen.fill(); pen.stroke();
  const ownship = scene.primitives.add(new Cesium.BillboardCollection()).add({
    image: symbol, width: 40, height: 40, disableDepthTestDistance: Number.POSITIVE_INFINITY, show: false,
    alignedAxis: Cesium.Cartesian3.UNIT_Z,
  });

  const ahead = new Cesium.Cartesian3();
  const aircraftPosition = new Cesium.Cartesian3();
  const aircraftPlacement = new Cesium.Matrix4();
  const followCamera = aircraftCamera(Cesium, camera);
  let frame: { state: Live; air: AircraftSample; height: number } | null = null;
  const updateCamera = () => {
    const state = live.current;
    if (!state) return;
    const fraction = (performance.now() - state.at) / state.interval;
    const air = blendAircraft(state.from, state.to, fraction);
    // Still moving between two ticks: the next frame is needed too.
    if (fraction < 1) request("blend");
    const pose = cameraPose(air, state.view, state.layout);
    // The simulation knows nothing of terrain; the eye is kept above the ground it would otherwise fly through.
    const ground = scene.globe.getHeight(Cesium.Cartographic.fromDegrees(pose.longitude, pose.latitude));
    const height = state.view === "map" || ground === undefined ? pose.height : Math.max(pose.height, ground + 3);
    // Only a new aircraft pose moves the camera; retain its basis through Cesium's own angle-read rounding.
    followCamera({ ...pose, height });
    frame = { state, air, height };
  };
  const onFrame = () => {
    if (!frame) return;
    const { state, air, height } = frame;
    if (colouringChoice === "relative") relative.uniforms.aircraft = air.altitude * FT;
    obstacles.update(air.altitude, colouringChoice);
    const at = Cesium.Cartesian3.fromDegrees(air.position.lon, air.position.lat, air.altitude * FT, undefined, aircraftPosition);
    ownship.show = state.view === "map";
    ownship.position = at;
    ownship.rotation = -Cesium.Math.toRadians(air.heading);
    const chase = state.view === "chase";
    for (const model of models) model.show = chase && !helicopter.loaded;
    if (chase) {
      // The model's +x is forward; Cesium's heading turns +x from east, so north-up heading is a quarter turn less.
      orientation.heading = Cesium.Math.toRadians(air.heading - 90);
      orientation.pitch = Cesium.Math.toRadians(air.pitch);
      orientation.roll = Cesium.Math.toRadians(air.bank);
      const placed = Cesium.Transforms.headingPitchRollToFixedFrame(at, orientation, undefined, undefined, aircraftPlacement);
      for (const model of models) model.modelMatrix = placed;
      helicopter.update(placed, (performance.now() / 1000) * MAIN_ROTOR_RAD_S, true);
    } else helicopter.update(undefined, 0, false);

    // The flight path marker sits where the aircraft is going: a point 2 NM along the flight path, projected.
    if (state.view === "cockpit" && state.layout === "hud") {
      const distance = 3704, rad = Cesium.Math.toRadians;
      const north = Math.cos(rad(air.heading)) * distance, east = Math.sin(rad(air.heading)) * distance;
      Cesium.Cartesian3.fromDegrees(
        air.position.lon + east / (111_320 * Math.cos(rad(air.position.lat))), air.position.lat + north / 110_540,
        height + Math.tan(rad(air.pitch)) * distance, undefined, ahead);
      const at = scene.cartesianToCanvasCoordinates(ahead);
      if (at) pathMarker.style.transform = `translate(${at.x}px, ${at.y}px) translate(-50%, -50%) rotate(${-air.bank}deg)`;
    }
  };
  // preUpdate runs after initializeFrame and before Cesium tests the camera for demand rendering. preRender
  // runs only once that decision has been made, too late to keep a frozen aircraft's camera fixed (#1298).
  scene.preUpdate.addEventListener(updateCamera);
  // Models, obstacle colours and HUD projections only change on a drawn frame, including when paused.
  scene.preRender.addEventListener(onFrame);
  // The frames drawn, on the scene element: the scene draws only on change, and this is how that can be seen (and tested),
  let frames = 0;
  // With whether the globe has every tile it needs: until then Cesium keeps drawing as tiles arrive.
  const causes: Record<string, number> = {};
  const lastView = new Cesium.Matrix4();
  let tilesWereLoading = true;
  const counted = () => {
    container.dataset.frames = String(++frames);
    container.dataset.tilesLoaded = String(scene.globe.tilesLoaded);
    progressed(true, scene.globe.tilesLoaded && tileQueue === 0);
    const cause = asked ? "asked" : !Cesium.Matrix4.equals(camera.viewMatrix, lastView) ? "camera" : tilesWereLoading ? "tiles"
      : internal.size ? [...internal].sort().join("+") : "unattributed";
    causes[cause] = (causes[cause] ?? 0) + 1;
    container.dataset.frameCauses = Object.entries(causes).map(([why, count]) => `${why} ${count}`).join(", ");
    asked = false;
    internal.clear();
    Cesium.Matrix4.clone(camera.viewMatrix, lastView);
    tilesWereLoading = !scene.globe.tilesLoaded;
  };
  scene.postRender.addEventListener(counted);

  // Only rendering follows the visible document. Aircraft interpolation above keeps the owner's performance
  // clock; a child's RAF timestamp is used solely to cap this loop, never as simulation/interpolation time.
  let destroyed = false, renderFailed = false;
  const renderLoop = aircraftRenderLoop({
    window: renderingWindow, canvas: widget.canvas,
    unavailable: () => disposed() || widget.isDestroyed() || renderFailed,
    render: () => {
      widget.resize();
      const before = frames;
      widget.render();
      return frames !== before;
    },
    onFailure: error => { renderFailed = true; onFailure(error); },
  });
  const removeRenderError = scene.renderError.addEventListener((_scene: unknown, error: unknown) => {
    renderFailed = true; renderLoop.destroy(); onFailure(error);
  });

  return {
    requestRender: () => request("tick"),
    setGround,
    setColouring,
    setRoute: (route, altitude) => {
      request("route");
      routeLine.removeAll(); fixes.removeAll(); labels.removeAll();
      if (!route.length) return;
      const heightsAt = routeHeights(route.map(point => constraintAltitude(point.constraint ?? undefined)), altitude);
      const positions = route.map((point, index) => Cesium.Cartesian3.fromDegrees(point.position.lon, point.position.lat, heightsAt[index]));
      if (positions.length > 1) {
        routeLine.add({ positions, width: 3, material: Cesium.Material.fromType("Color", { color: magenta.withAlpha(0.9) }) });
      }
      route.forEach((point, index) => {
        fixes.add({ position: positions[index], pixelSize: 9, color: point.active ? magenta : Cesium.Color.WHITE, outlineColor: magenta, outlineWidth: 2,
          disableDepthTestDistance: Number.POSITIVE_INFINITY });
        labels.add({
          position: positions[index], text: point.constraint ? `${point.ident}\n${point.constraint}` : point.ident,
          font: "600 13px sans-serif", fillColor: point.active ? magenta : Cesium.Color.WHITE, outlineColor: Cesium.Color.BLACK, outlineWidth: 3,
          style: Cesium.LabelStyle.FILL_AND_OUTLINE, pixelOffset: new Cesium.Cartesian2(10, -10), horizontalOrigin: Cesium.HorizontalOrigin.LEFT,
          disableDepthTestDistance: Number.POSITIVE_INFINITY,
        });
      });
    },
    destroy: () => {
      if (destroyed) return;
      destroyed = true;
      stopTerrainWatch();
      renderLoop.destroy();
      removeRenderError();
      helicopter.destroy();
      obstacles.destroy();
      scene.preUpdate.removeEventListener(updateCamera);
      scene.preRender.removeEventListener(onFrame);
      scene.postRender.removeEventListener(counted);
      stopQueueWatch();
      shader.dispose();
      if (!widget.isDestroyed()) widget.destroy();
    },
  };
}
