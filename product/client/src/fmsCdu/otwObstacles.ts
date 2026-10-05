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

/**
 * A thrown value in words. Cesium passes a web worker's error on as the plain object the worker posted ({ name, message,
 * stack }) unless its name is Error, RuntimeError or DeveloperError, so a TypeError from a worker would otherwise read
 * "[object Object]" (#1492).
 */
export function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === "string") return error;
  if (error && typeof error === "object") {
    const { name, message } = error as { name?: unknown; message?: unknown };
    if (typeof message === "string") return typeof name === "string" && name ? `${name}: ${message}` : message;
    try { return JSON.stringify(error); } catch { /* not serialisable: fall through */ }
  }
  return String(error);
}

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
    return { obstacle, point, key: -1, attributes: null as { color: Uint8Array; boundingSphere?: unknown } | null };
  });
  const lines = instances.length ? new Cesium.Primitive({ geometryInstances: instances,
    appearance: new Cesium.PolylineColorAppearance({ translucent: false }), asynchronous: true, show: false }) : null;
  // A failed asynchronous build (a geometry worker's error, such as a worker module that could not be fetched) is thrown
  // by Cesium from the primitive's update on every frame, as a render error that stops the whole view (#1492). The scene
  // updates the lines through this guard, which fails the layer with that error instead and leaves the view drawing.
  // Once drawn, a later error is the scene's again.
  const guard = lines && {
    update(frameState: unknown) {
      if (finished && !visible) return; // Failed or destroyed: the removal follows.
      try { (lines as unknown as { update(frameState: unknown): void }).update(frameState); } catch (error) {
        if (visible) throw error;
        fail(`obstacle geometry failed: ${describeError(error)}`, true);
      }
    },
    isDestroyed: () => lines.isDestroyed(),
    destroy() { if (!lines.isDestroyed()) lines.destroy(); },
  };
  if (guard) scene.primitives.add(guard);
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
    if (guard) scene.primitives.remove(guard);
    scene.primitives.remove(tops);
  };
  const fail = (reason: string, inRender = false) => {
    if (finished) return;
    finished = true;
    stopWatching();
    resolveReady({ failed: `obstacles not drawn: ${reason}` });
    if (inRender) {
      // Do not alter Cesium's primitive list from inside its own render/error event traversal.
      removalDeferred = true;
      queueMicrotask(() => { removalDeferred = false; remove(); });
    } else remove();
  };
  const recolour = () => {
    const { altitude, mode } = latest;
    // Only relative colouring depends on aircraft altitude; keep its cumulative 10-ft update threshold.
    if (last && last.mode === mode && (mode !== "relative" || Math.abs(last.altitude - altitude) < 10)) return;
    last = { altitude, mode };
    let changed = false;
    for (const item of drawn) {
      // The palette uses integer RGB bytes, so this value preserves colour equality without a string allocation.
      const rgb = obstacleColour(item.obstacle, altitude, mode), key = (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
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
  // A scene that stops rendering before the layer has drawn leaves it undrawn, whatever raised the error; say which.
  stopErrorWatch = scene.renderError.addEventListener((_scene, error) => fail(`the scene stopped rendering: ${describeError(error)}`, true));
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
    } catch (error) { fail(describeError(error), true); }
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
  }).catch((error: unknown) => ({ failed: `obstacle data not loaded: ${describeError(error)}` }));
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
