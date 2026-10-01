import { NEUTRAL_RGB, obstacleColour, parseDof, type Obstacle } from "./obstacles";
import type { TerrainColouring } from "./terrainAwareness";

/**
 * The FAA DOF obstacles in the out-the-window scene: each a vertical line from its base (its top less its height above
 * ground) to its top at true height, with a point at the top, coloured opaque as the terrain colouring colours its top
 * (obstacles.ts obstacleColour, terrainAwareness.ts). Heights are feet above mean sea level, placed as the view places the aircraft (metres,
 * the same datum). The data is fetched once from the bench's own origin (DEC-047).
 */
export const OBSTACLE_DATA_URL = "fms-cdu/obstacles/dof-bench-extract.csv";
const FT = 0.3048;

/** The parts of Cesium the layer uses, so tests can pass a stand-in without loading the renderer. */
export type ObstacleCesium = Pick<typeof import("@cesium/engine"), "Primitive" | "GeometryInstance" | "PolylineGeometry"
  | "PolylineColorAppearance" | "ColorGeometryInstanceAttribute" | "ArcType" | "PointPrimitiveCollection" | "Cartesian3" | "Color">;
export type ObstacleScene = {
  primitives: { add<T>(primitive: T): T; remove(primitive: unknown): boolean; isDestroyed(): boolean };
  postRender: { addEventListener(listener: () => void): () => void };
  renderError: { addEventListener(listener: (_scene: unknown, error: unknown) => void): () => void };
  requestRender?: () => void;
};
type Outcome = { drawn: number } | { failed: string };

export type ObstacleLayer = {
  /** Recolours by clearance when the aircraft altitude or the colouring mode changes. */
  update(aircraftAltitudeFt: number, mode: TerrainColouring): void;
  destroy(): void;
  /** The obstacles drawn (after the data has loaded). */
  readonly count: number;
  /** Resolves once the data has been read and drawn; the reason when it could not be. */
  readonly ready: Promise<Outcome>;
};

/** Builds the layer from obstacles already read (the scene part, separate from loading, so it can be tested). */
export function drawObstacles(Cesium: ObstacleCesium, scene: ObstacleScene, obstacles: readonly Obstacle[]) {
  // Positions never change. A static batch keeps all segments while avoiding Cesium's dynamic
  // polyline collection serializing every material on every frame (11,973 in the bench extract).
  const tops = new Cesium.PointPrimitiveCollection();
  tops.show = false;
  const instances: InstanceType<ObstacleCesium["GeometryInstance"]>[] = [];
  const neutral = Cesium.Color.fromBytes(...NEUTRAL_RGB);
  const drawn = obstacles.map((obstacle, id) => {
    const base = Cesium.Cartesian3.fromDegrees(obstacle.position.lon, obstacle.position.lat, (obstacle.amslFt - obstacle.aglFt) * FT);
    const top = Cesium.Cartesian3.fromDegrees(obstacle.position.lon, obstacle.position.lat, obstacle.amslFt * FT);
    // AGL zero is a valid DOF record: its base and top coincide. Cesium discards such a segment,
    // but the top point must remain, without preventing the other geometry from being drawn.
    if (obstacle.aglFt > 0) instances.push(new Cesium.GeometryInstance({
      id,
      geometry: new Cesium.PolylineGeometry({ positions: [base, top], width: 2, arcType: Cesium.ArcType.NONE,
        vertexFormat: Cesium.PolylineColorAppearance.VERTEX_FORMAT }),
      attributes: { color: Cesium.ColorGeometryInstanceAttribute.fromColor(neutral) },
    }));
    const point = tops.add({ position: top, pixelSize: 5, color: neutral });
    return { obstacle, point, key: "", attributes: null as { color: Uint8Array; boundingSphere?: unknown } | null };
  });
  const lines = instances.length ? scene.primitives.add(new Cesium.Primitive({ geometryInstances: instances,
    appearance: new Cesium.PolylineColorAppearance({ translucent: false }), asynchronous: true, show: false })) : null;
  scene.primitives.add(tops);
  let latest: { altitude: number; mode: TerrainColouring } = { altitude: 0, mode: "off" };
  let last: { altitude: number; mode: TerrainColouring } | null = null;
  let initialized = false, visible = false, finished = false, destroyed = false, removed = false, removalDeferred = false;
  let resolveReady: (outcome: Outcome) => void = () => {};
  const ready = new Promise<Outcome>(resolve => { resolveReady = resolve; });
  let stopReadyWatch = () => {}, stopErrorWatch = () => {};
  const stopWatching = () => { stopReadyWatch(); stopErrorWatch(); };
  const remove = () => {
    if (removed || removalDeferred) return;
    removed = true;
    if (scene.primitives.isDestroyed()) return;
    if (lines) scene.primitives.remove(lines);
    scene.primitives.remove(tops);
  };
  const fail = (error: unknown, inRender = false) => {
    if (finished) return;
    finished = true;
    stopWatching();
    resolveReady({ failed: `obstacles not drawn: ${error instanceof Error ? error.message : String(error)}` });
    if (inRender) {
      // Do not alter Cesium's primitive list from inside its own render/error event traversal.
      removalDeferred = true;
      queueMicrotask(() => { removalDeferred = false; remove(); });
    } else remove();
  };
  const recolour = () => {
    const { altitude, mode } = latest;
    // Recolour only when something that decides a colour has changed by a visible amount.
    if (last && last.mode === mode && Math.abs(last.altitude - altitude) < 10) return;
    last = { altitude, mode };
    let changed = false;
    for (const item of drawn) {
      const rgb = obstacleColour(item.obstacle, altitude, mode), key = rgb.join(",");
      if (key === item.key) continue;
      item.key = key;
      const colour = Cesium.Color.fromBytes(rgb[0], rgb[1], rgb[2]);
      item.point.color = colour;
      // Assign through Cesium's attribute setter; mutating the returned bytes alone does not upload a change.
      if (item.attributes) item.attributes.color = Cesium.ColorGeometryInstanceAttribute.toValue(colour);
      changed = true;
    }
    if (changed) scene.requestRender?.();
  };
  // Cesium marks a failed primitive ready too. Observe render errors as well as a rendered frame,
  // and validate its actual instance attributes before exposing either the lines or their top markers.
  stopErrorWatch = scene.renderError.addEventListener((_scene, error) => fail(error, true));
  stopReadyWatch = scene.postRender.addEventListener(() => {
    if ((lines && !lines.ready) || finished || destroyed) return;
    if (initialized) {
      // The frame after exposure has now rendered successfully, including its appearance/shader.
      visible = finished = true;
      stopWatching();
      resolveReady({ drawn: drawn.length });
      return;
    }
    try {
      drawn.forEach((item, id) => {
        if (item.obstacle.aglFt === 0) return;
        const attributes = lines!.getGeometryInstanceAttributes(id) as typeof item.attributes;
        if (!attributes?.boundingSphere || attributes.color?.length !== 4) throw new Error(`obstacle ${item.obstacle.oas} has no rendered geometry`);
        item.attributes = attributes;
      });
      recolour();
      if (lines) lines.show = true;
      tops.show = initialized = true;
      scene.requestRender?.();
    } catch (error) { fail(error, true); }
  });
  return {
    lines, tops, drawn, ready,
    get count() { return visible ? drawn.length : 0; },
    update(aircraftAltitudeFt: number, mode: TerrainColouring) {
      if (destroyed) return;
      latest = { altitude: aircraftAltitudeFt, mode };
      if (initialized && (!finished || visible)) recolour();
    },
    destroy() {
      if (destroyed) return;
      destroyed = true;
      visible = false;
      fail("destroyed before geometry was ready");
      stopWatching();
      remove();
    },
  };
}

/**
 * The obstacle layer: fetches the bench extract, reads it and draws it. Until the data has loaded, update only
 * remembers the latest altitude and mode, and applies them once drawn.
 */
export function createObstacleLayer(Cesium: ObstacleCesium, scene: ObstacleScene, fetchText: (url: string) => Promise<string> = defaultFetch): ObstacleLayer {
  let layer: ReturnType<typeof drawObstacles> | null = null;
  let pending: { altitude: number; mode: TerrainColouring } | null = null;
  let destroyed = false;
  const ready = fetchText(OBSTACLE_DATA_URL).then(text => {
    const { obstacles, errors } = parseDof(text);
    if (!obstacles.length) return { failed: errors[0] ?? "no obstacles in the data" };
    if (destroyed) return { failed: "destroyed before the data loaded" };
    layer = drawObstacles(Cesium, scene, obstacles);
    if (pending) layer.update(pending.altitude, pending.mode);
    scene.requestRender?.();
    return layer.ready;
  }).catch((error: unknown) => ({ failed: `obstacle data not loaded: ${error instanceof Error ? error.message : String(error)}` }));
  return {
    get count() { return layer?.count ?? 0; },
    ready,
    update(altitude, mode) { pending = { altitude, mode }; layer?.update(altitude, mode); },
    destroy() { destroyed = true; layer?.destroy(); layer = null; },
  };
}

async function defaultFetch(url: string) {
  const response = await fetch(`${import.meta.env.BASE_URL}${url}`);
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.text();
}
