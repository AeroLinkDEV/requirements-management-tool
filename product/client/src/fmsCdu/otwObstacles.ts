import { BAND_COLOURS, obstacleBand, parseDof, type Obstacle, type ObstacleColouring } from "./obstacles";

/**
 * The FAA DOF obstacles in the out-the-window scene: each a vertical line from its base (its top less its height above
 * ground) to its top at true height, with a point at the top, coloured by clearance (obstacles.ts obstacleBand) in the
 * terrain colouring mode. Heights are feet above mean sea level, placed as the view places the aircraft (metres,
 * the same datum). The data is fetched once from the bench's own origin (DEC-047).
 */
export const OBSTACLE_DATA_URL = "fms-cdu/obstacles/dof-bench-extract.csv";
const FT = 0.3048;

/** The parts of Cesium the layer uses, so tests can pass a stand-in. */
export type ObstacleCesium = {
  PolylineCollection: new () => { add(options: { positions: unknown[]; width: number; material?: unknown }): { material: unknown }; destroy?: () => void };
  PointPrimitiveCollection: new () => { add(options: { position: unknown; pixelSize: number; color: unknown }): { color: unknown }; destroy?: () => void };
  Cartesian3: { fromDegrees(lon: number, lat: number, height: number): unknown };
  Color: { fromBytes(r: number, g: number, b: number, a?: number): unknown };
  Material: { fromType(type: string, uniforms: Record<string, unknown>): unknown };
};
export type ObstacleScene = { primitives: { add<T>(primitive: T): T; remove(primitive: unknown): boolean }; requestRender?: () => void };

export type ObstacleLayer = {
  /** Recolours by clearance when the aircraft altitude or the colouring mode changes. */
  update(aircraftAltitudeFt: number, mode: ObstacleColouring): void;
  destroy(): void;
  /** The obstacles drawn (after the data has loaded). */
  readonly count: number;
  /** Resolves once the data has been read and drawn; the reason when it could not be. */
  readonly ready: Promise<{ drawn: number } | { failed: string }>;
};

/** Builds the layer from obstacles already read (the scene part, separate from loading, so it can be tested). */
export function drawObstacles(Cesium: ObstacleCesium, scene: ObstacleScene, obstacles: readonly Obstacle[]) {
  const lines = scene.primitives.add(new Cesium.PolylineCollection());
  const tops = scene.primitives.add(new Cesium.PointPrimitiveCollection());
  const drawn = obstacles.map(obstacle => {
    const base = Cesium.Cartesian3.fromDegrees(obstacle.position.lon, obstacle.position.lat, (obstacle.amslFt - obstacle.aglFt) * FT);
    const top = Cesium.Cartesian3.fromDegrees(obstacle.position.lon, obstacle.position.lat, obstacle.amslFt * FT);
    const line = lines.add({ positions: [base, top], width: 2 });
    const point = tops.add({ position: top, pixelSize: 5, color: Cesium.Color.fromBytes(...BAND_COLOURS.clear) });
    return { obstacle, line, point, band: "" };
  });
  let last: { altitude: number; mode: ObstacleColouring } | null = null;
  return {
    lines, tops, drawn,
    update(aircraftAltitudeFt: number, mode: ObstacleColouring) {
      // Recolour only when something that decides a colour has changed by a visible amount.
      if (last && last.mode === mode && Math.abs(last.altitude - aircraftAltitudeFt) < 10) return;
      last = { altitude: aircraftAltitudeFt, mode };
      let changed = false;
      for (const item of drawn) {
        const band = obstacleBand(item.obstacle, aircraftAltitudeFt, mode);
        if (band === item.band) continue;
        item.band = band;
        const colour = Cesium.Color.fromBytes(...BAND_COLOURS[band]);
        item.point.color = colour;
        item.line.material = Cesium.Material.fromType("Color", { color: colour });
        changed = true;
      }
      if (changed) scene.requestRender?.();
    },
    destroy() {
      scene.primitives.remove(lines);
      scene.primitives.remove(tops);
    },
  };
}

/**
 * The obstacle layer: fetches the bench extract, reads it and draws it. Until the data has loaded, update only
 * remembers the latest altitude and mode, and applies them once drawn.
 */
export function createObstacleLayer(Cesium: ObstacleCesium, scene: ObstacleScene, fetchText: (url: string) => Promise<string> = defaultFetch): ObstacleLayer {
  let layer: ReturnType<typeof drawObstacles> | null = null;
  let pending: { altitude: number; mode: ObstacleColouring } | null = null;
  let destroyed = false;
  const ready = fetchText(OBSTACLE_DATA_URL).then(text => {
    const { obstacles, errors } = parseDof(text);
    if (!obstacles.length) return { failed: errors[0] ?? "no obstacles in the data" };
    if (destroyed) return { failed: "destroyed before the data loaded" };
    layer = drawObstacles(Cesium, scene, obstacles);
    if (pending) layer.update(pending.altitude, pending.mode);
    scene.requestRender?.();
    return { drawn: obstacles.length };
  }, (error: unknown) => ({ failed: `obstacle data not loaded: ${error instanceof Error ? error.message : String(error)}` }));
  return {
    get count() { return layer?.drawn.length ?? 0; },
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
