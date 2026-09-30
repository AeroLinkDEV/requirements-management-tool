import { TILE_PIXELS, decodeTerrarium } from "./outTheWindow";

/**
 * The FMS bench's height tiles, shared by the out-the-window view and the PFD's synthetic vision: each tile fetched
 * once, decoded once, and read either as a whole (the 3D view cuts meshes and imagery from it) or a point at a time
 * (the PFD samples along its rays).
 */

/** Where height tiles come from: a Terrarium PNG, a 404 for a tile that does not exist, or a failure. */
export type TerrainSource = (z: number, x: number, y: number) => Promise<Response>;
/** Decodes a Terrarium PNG to RGBA; the browser's image decoder by default, replaceable in tests. */
export type TileDecoder = (image: Blob) => Promise<ArrayLike<number>>;
/** What the source last said: nothing yet, tiles arriving, turned off on this installation, or not answering. */
export type TerrainStatus = "waiting" | "live" | "off" | "unreachable";

const browserDecoder = (): TileDecoder => {
  let scratch: CanvasRenderingContext2D | null = null;
  return async image => {
    scratch ??= Object.assign(document.createElement("canvas"), { width: TILE_PIXELS, height: TILE_PIXELS }).getContext("2d", { willReadFrequently: true })!;
    const bitmap = await createImageBitmap(image, { colorSpaceConversion: "none", premultiplyAlpha: "none" });
    scratch.clearRect(0, 0, TILE_PIXELS, TILE_PIXELS);
    scratch.drawImage(bitmap, 0, 0);
    bitmap.close();
    return scratch.getImageData(0, 0, TILE_PIXELS, TILE_PIXELS).data;
  };
};

/** Web Mercator tile coordinates of a point at a zoom, with the fraction across the tile. */
export function tileOf(lat: number, lon: number, zoom: number) {
  const scale = 2 ** zoom;
  const u = ((lon + 180) / 360) * scale;
  const rad = (Math.max(-85.0511, Math.min(85.0511, lat)) * Math.PI) / 180;
  const v = ((1 - Math.log(Math.tan(rad) + 1 / Math.cos(rad)) / Math.PI) / 2) * scale;
  const x = Math.floor(u), y = Math.floor(v);
  return { x: ((x % scale) + scale) % scale, y: Math.min(scale - 1, Math.max(0, y)), fx: u - x, fy: v - y };
}

/**
 * How many height tiles are kept (256 KB each: about 100 MB). The least recently used beyond it are dropped and fetched
 * again if needed; without a bound, a long flight kept every tile it had passed over, hundreds of megabytes.
 */
export const TERRAIN_TILE_CAPACITY = 384;

export class TerrainTiles {
  // Both in least-recently-used order: a use moves a tile to the end, and the oldest settled tiles go first.
  private readonly tiles = new Map<string, Promise<Float32Array | null>>();
  private readonly ready = new Map<string, Float32Array | null>();
  private readonly listeners = new Set<() => void>();
  private current: TerrainStatus = "waiting";
  private readonly source: TerrainSource;
  private readonly decode: TileDecoder;
  private readonly capacity: number;

  constructor(source: TerrainSource, decode: TileDecoder = browserDecoder(), capacity = TERRAIN_TILE_CAPACITY) {
    this.source = source;
    this.decode = decode;
    this.capacity = capacity;
  }

  /** How many tiles are held now (loading or loaded). */
  get size() { return this.tiles.size; }

  get status(): TerrainStatus { return this.current; }

  /** Called when a tile arrives or the status changes; returns the unsubscribe. */
  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /** A tile's heights, row 0 north, or null where the source has none or failed. */
  load(z: number, x: number, y: number): Promise<Float32Array | null> {
    const key = `${z}/${x}/${y}`;
    let tile = this.tiles.get(key);
    if (tile) { this.touch(key); return tile; }
    tile = this.fetch(z, x, y).catch(() => null).then(heights => {
      // Dropped while it was loading: the caller still gets it, but it is not kept.
      if (this.tiles.get(key) === tile) { this.ready.set(key, heights); this.evict(); }
      this.changed();
      return heights;
    });
    this.tiles.set(key, tile);
    return tile;
  }

  /** Marks a tile as just used. */
  private touch(key: string) {
    const tile = this.tiles.get(key);
    if (tile) { this.tiles.delete(key); this.tiles.set(key, tile); }
    if (this.ready.has(key)) { const heights = this.ready.get(key)!; this.ready.delete(key); this.ready.set(key, heights); }
  }

  /** Drops the least recently used loaded tiles beyond the capacity; tiles still loading are never dropped. */
  private evict() {
    for (const key of this.tiles.keys()) {
      if (this.tiles.size <= this.capacity) return;
      if (!this.ready.has(key)) continue;
      this.tiles.delete(key);
      this.ready.delete(key);
    }
  }

  /**
   * The ground height at a point from the tiles at `zoom`, bilinear, or null while its tile is still loading (which
   * starts it loading) or where there is no data. Below sea level reads as sea level, as in the 3D view.
   */
  heightAt(lat: number, lon: number, zoom: number): number | null {
    const { x, y, fx, fy } = tileOf(lat, lon, zoom);
    const key = `${zoom}/${x}/${y}`;
    const heights = this.ready.get(key);
    if (heights === undefined) { void this.load(zoom, x, y); return null; }
    this.touch(key);
    if (heights === null) return null;
    const last = TILE_PIXELS - 1;
    const px = Math.min(last, fx * TILE_PIXELS), py = Math.min(last, fy * TILE_PIXELS);
    const x0 = Math.floor(px), y0 = Math.floor(py), x1 = Math.min(x0 + 1, last), y1 = Math.min(y0 + 1, last);
    const tx = px - x0, ty = py - y0;
    const top = heights[y0 * TILE_PIXELS + x0] * (1 - tx) + heights[y0 * TILE_PIXELS + x1] * tx;
    const bottom = heights[y1 * TILE_PIXELS + x0] * (1 - tx) + heights[y1 * TILE_PIXELS + x1] * tx;
    return Math.max(0, top * (1 - ty) + bottom * ty);
  }

  private async fetch(z: number, x: number, y: number): Promise<Float32Array | null> {
    let response: Response;
    try {
      response = await this.source(z, x, y);
    } catch {
      this.report("unreachable");
      return null;
    }
    if (!response.ok) {
      const body = response.status === 404 ? await response.json().catch(() => null) as { code?: string } | null : null;
      // A tile the source does not publish is ocean or nothing: flat, and not a failure.
      if (body?.code === "terrain_relay_disabled") this.report("off");
      else if (response.status !== 404) this.report("unreachable");
      return null;
    }
    const heights = decodeTerrarium(await this.decode(await response.blob()));
    this.report("live");
    return heights;
  }

  // Off is an installation's setting and stays; otherwise the latest answer stands.
  private report(status: TerrainStatus) {
    if (this.current === status || this.current === "off") return;
    this.current = status;
    this.changed();
  }

  private changed() { for (const listener of this.listeners) listener(); }
}
