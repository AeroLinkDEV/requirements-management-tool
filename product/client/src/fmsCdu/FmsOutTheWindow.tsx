import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import type { AircraftData, RoutePoint } from "./efis";
import { constraintAltitude } from "./flight";
import {
  AIRCRAFT_PARTS, FT, MESH_MAX_ZOOM, RELIEF_MAX_ZOOM, TILE_PIXELS, ancestorOf, blendAircraft, cameraPose, pixelMetres,
  routeHeights, sampleHeights, shadeTile, tileLatitude, type AircraftSample, type Layout, type View,
} from "./outTheWindow";
import { workerReliefShader } from "./reliefShader";
import type { TerrainTiles } from "./terrainTiles";
import "./FmsOutTheWindow.css";

/** The modes on the flight mode annunciator, as the bench's Flight card shows them. */
export type HudModes = { lateral: string; vertical: string; armed: string[]; angleReference?: "MAG" | "TRUE"; magneticVariation?: number };

type Props = { air: AircraftData; route: RoutePoint[]; modes: HudModes; layout: Layout; view: View; tiles: TerrainTiles };

type Status = "loading" | "ready" | "no-webgl" | "failed";

/** Heights per side of a terrain mesh tile. */
const MESH_SAMPLES = 33;
const ROUTE_MAGENTA = "#e04cd6";

// Cesium fetches its workers and assets at run time from here: the development server answers it from the package,
// and a build copies them beside the bundle (vite.config.ts), so they come from this server either way (DEC-047).
const CESIUM_BASE = `${import.meta.env.BASE_URL}cesium/`;

type Live = { from: AircraftSample; to: AircraftSample; at: number; interval: number; view: View; layout: Layout };
type SceneHandle = { setRoute: (route: RoutePoint[], altitude: number) => void; requestRender: () => void; destroy: () => void };

/**
 * The view out of the aircraft, drawn with CesiumJS over open elevation data: the ground coloured by height and
 * shaded by slope, the way a synthetic vision system draws it, with the active route in magenta. The camera follows
 * the simulated aircraft (efis.ts `aircraftData`), so it shows what the flight model is doing, not a separate game.
 *
 * Two layouts: HUD puts the primary flight data over a large window; Panel is a shorter window over a glareshield, as a
 * seated pilot sees it, with the bench's CDU, PFD and ND below it standing in for the instrument panel. The engine is
 * loaded only when this is first shown.
 */
export default function FmsOutTheWindow({ air, route, modes, layout, view, tiles }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const credits = useRef<HTMLDivElement>(null);
  const pathMarker = useRef<HTMLDivElement>(null);
  const scene = useRef<SceneHandle | null>(null);
  const [status, setStatus] = useState<Status>("loading");
  const [failure, setFailure] = useState("");
  const terrain = useSyncExternalStore(listener => tiles.subscribe(listener), () => tiles.status);

  // The scene reads this every frame; renders only move its target.
  const live = useRef<Live | null>(null);
  useLayoutEffect(() => {
    // The helicopter profile's hover-data flag, where the aircraft data carries one.
    const hoverData = (air as AircraftData & { helicopter?: { hoverData?: boolean } | null }).helicopter?.hoverData === true;
    const sample: AircraftSample = { position: air.position, altitude: air.altitude, heading: air.heading, pitch: air.pitch, bank: air.bank, hoverData };
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
    startScene(host.current!, credits.current!, pathMarker.current!, live, tiles)
      .then(created => {
        if (disposed) { created.destroy(); return; }
        handle = created;
        scene.current = created;
        setStatus("ready");
      }, (error: unknown) => {
        if (disposed) return;
        setFailure(error instanceof Error ? error.message : String(error));
        setStatus(/webgl/i.test(String(error)) ? "no-webgl" : "failed");
      });
    return () => { disposed = true; handle?.destroy(); scene.current = null; };
  }, [tiles]);

  const routeKey = route.map(point => `${point.ident}:${point.position.lat},${point.position.lon}:${point.constraint ?? ""}:${point.active}`).join("|");
  useEffect(() => {
    scene.current?.setRoute(route, air.altitude);
  }, [routeKey, status]); // eslint-disable-line react-hooks/exhaustive-deps

  const hud = layout === "hud" && view === "cockpit";
  const note = status === "no-webgl" ? "This browser cannot draw 3D graphics (WebGL is unavailable), so the view is off."
    : status === "failed" ? `The 3D view could not start: ${failure}`
    : terrain === "off" ? "Terrain data is off on this installation, so the ground is drawn flat. An administrator can turn it on with the FmsBench:TerrainRelay setting (the server then fetches open elevation tiles from AWS)."
    : terrain === "unreachable" ? "The server cannot reach the terrain source, so the ground is drawn flat where tiles are missing."
    : null;

  return (
    <div className={`fmsOtw layout-${layout} view-${view}`} data-status={status} data-terrain={terrain}>
      <div className="fmsOtwScene" ref={host} />
      {layout === "panel" && view === "cockpit" ? <div className="fmsOtwGlareshield" aria-hidden="true" /> : null}
      <div className="fmsOtwPathMarker" ref={pathMarker} hidden={!hud || status !== "ready"} aria-hidden="true">
        <svg viewBox="-40 -14 80 28"><circle r="7" /><path d="M-7 0 H-30 M7 0 H30 M0 -7 V-14" /></svg>
      </div>
      {hud ? <Hud air={air} modes={modes} /> : null}
      {status === "loading" ? <p className="fmsOtwNote" role="status">Loading the 3D view…</p> : null}
      {note ? <p className="fmsOtwNote" role="status">{note}</p> : null}
      <div className="fmsOtwCredits">
        <div ref={credits} />
        <span>Terrain: Mapzen Terrain Tiles on AWS Open Data (SRTM, GMTED2010, USGS NED and others). Route fixes are invented.</span>
      </div>
    </div>
  );
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
const canvas = (size = TILE_PIXELS) => Object.assign(document.createElement("canvas"), { width: size, height: size });

async function startScene(
  container: HTMLElement, creditContainer: HTMLElement, pathMarker: HTMLElement, live: { current: Live | null },
  tiles: TerrainTiles,
): Promise<SceneHandle> {
  (window as unknown as { CESIUM_BASE_URL: string }).CESIUM_BASE_URL = CESIUM_BASE;
  // The engine alone: the `cesium` package's widgets evaluate a string as script, which the policy refuses.
  const Cesium = await import("@cesium/engine");

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

  // The ground's colour is drawn from the same heights: no photographic imagery, so no imagery licence. It is shaded
  // in a worker (reliefShader.ts), so a burst of new tiles does not freeze the page.
  const shader = workerReliefShader();
  const flat = canvas();
  flat.getContext("2d")!.putImageData(new ImageData(shadeTile(new Float32Array(TILE_PIXELS * TILE_PIXELS), 30), TILE_PIXELS, TILE_PIXELS), 0, 0);
  const relief = {
    tilingScheme, rectangle: tilingScheme.rectangle, tileWidth: TILE_PIXELS, tileHeight: TILE_PIXELS,
    minimumLevel: 0, maximumLevel: RELIEF_MAX_ZOOM, hasAlphaChannel: false, ready: true,
    errorEvent: new Cesium.Event(), credit: undefined, proxy: undefined, tileDiscardPolicy: undefined,
    getTileCredits: () => [],
    pickFeatures: () => undefined,
    requestImage: async (x: number, y: number, level: number) => {
      const tile = await heights(level, x, y);
      if (!tile) return flat;
      const rgba = await shader.shade(tile, pixelMetres(level, tileLatitude(level, y)));
      const image = canvas();
      image.getContext("2d")!.putImageData(new ImageData(rgba, TILE_PIXELS, TILE_PIXELS), 0, 0);
      return image;
    },
  } as unknown as InstanceType<typeof Cesium.UrlTemplateImageryProvider>;

  // Throws when the browser has no WebGL; the component says so rather than failing the page.
  const widget = new Cesium.CesiumWidget(container, {
    baseLayer: new Cesium.ImageryLayer(relief), terrainProvider, creditContainer,
    skyBox: false, showRenderLoopErrors: false, targetFrameRate: 30, useBrowserRecommendedResolution: true,
    // Draw only when something changes: a paused bench, or a view waiting for the next tick, costs nothing. Cesium
    // itself asks for frames while tiles load and when the window is resized.
    requestRenderMode: true, maximumRenderTimeChange: Number.POSITIVE_INFINITY,
  });
  const { scene, camera } = widget;
  scene.globe.depthTestAgainstTerrain = true;
  scene.globe.baseColor = Cesium.Color.fromBytes(74, 112, 62);
  scene.globe.maximumScreenSpaceError = 1.6;
  scene.fog.enabled = true;
  scene.fog.density = 1.4e-4;
  scene.screenSpaceCameraController.enableInputs = false;

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
  const orientation = new Cesium.HeadingPitchRoll();
  // The plan-view symbol: the rotor disc, the fuselage and the tail boom, nose up.
  const symbol = canvas(48);
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
  let lastPose = "";
  const onFrame = () => {
    const state = live.current;
    if (!state) return;
    const fraction = (performance.now() - state.at) / state.interval;
    const air = blendAircraft(state.from, state.to, fraction);
    // Still moving between two ticks: the next frame is needed too.
    if (fraction < 1) scene.requestRender();
    const pose = cameraPose(air, state.view, state.layout);
    // The simulation knows nothing of terrain; the eye is kept above the ground it would otherwise fly through.
    const ground = scene.globe.getHeight(Cesium.Cartographic.fromDegrees(pose.longitude, pose.latitude));
    const height = state.view === "map" || ground === undefined ? pose.height : Math.max(pose.height, ground + 3);
    // Only a new pose moves the camera: setting the same view again leaves rounding differences that Cesium, comparing
    // cameras to 1e-15, takes for a move, and a paused view then kept drawing several frames a second.
    const key = `${pose.longitude} ${pose.latitude} ${height} ${pose.heading} ${pose.pitch} ${pose.roll}`;
    if (key !== lastPose) {
      lastPose = key;
      camera.setView({
        destination: Cesium.Cartesian3.fromDegrees(pose.longitude, pose.latitude, height),
        orientation: { heading: pose.heading, pitch: pose.pitch, roll: pose.roll },
      });
    }
    const at = Cesium.Cartesian3.fromDegrees(air.position.lon, air.position.lat, air.altitude * FT);
    ownship.show = state.view === "map";
    ownship.position = at;
    ownship.rotation = -Cesium.Math.toRadians(air.heading);
    const chase = state.view === "chase";
    for (const model of models) model.show = chase;
    if (chase) {
      // The model's +x is forward; Cesium's heading turns +x from east, so north-up heading is a quarter turn less.
      orientation.heading = Cesium.Math.toRadians(air.heading - 90);
      orientation.pitch = Cesium.Math.toRadians(air.pitch);
      orientation.roll = Cesium.Math.toRadians(air.bank);
      const placed = Cesium.Transforms.headingPitchRollToFixedFrame(at, orientation);
      for (const model of models) model.modelMatrix = placed;
    }

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
  scene.preRender.addEventListener(onFrame);
  // The frames drawn, on the scene element: the scene draws only on change, and this is how that can be seen (and tested),
  let frames = 0;
  // With whether the globe has every tile it needs: until then Cesium keeps drawing as tiles arrive.
  const counted = () => { container.dataset.frames = String(++frames); container.dataset.tilesLoaded = String(scene.globe.tilesLoaded); };
  scene.postRender.addEventListener(counted);

  return {
    requestRender: () => scene.requestRender(),
    setRoute: (route, altitude) => {
      scene.requestRender();
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
      scene.preRender.removeEventListener(onFrame);
      scene.postRender.removeEventListener(counted);
      shader.dispose();
      if (!widget.isDestroyed()) widget.destroy();
    },
  };
}
