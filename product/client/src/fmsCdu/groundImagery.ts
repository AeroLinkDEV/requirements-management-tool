import { TILE_PIXELS } from "./outTheWindow";

/**
 * Aerial imagery for the out-the-window view's ground, through the server's relay (FmsBenchImageryEndpoints.cs): the
 * USGS National Map orthoimagery (US federal public-domain data, 6 inches to 1 metre, the United States only). Where
 * there is none, the view draws its elevation relief instead, tile by tile: the relay answers 404 outside the
 * coverage, and at low zoom levels near its edge the service fills a tile with blank white, which counts as none too.
 * Along the edge of the coverage (coastlines, the border) the service sends PNG, transparent where it has no imagery:
 * such a tile is drawn over its relief, and one with nothing in it at all counts as none.
 */

/** Where imagery tiles come from: a JPEG, a PNG along the edge of the coverage, a 404 where there is none, or a failure. */
export type ImagerySource = (z: number, x: number, y: number) => Promise<Response>;
/** Nothing yet; tiles arriving; turned off on this installation; or not answering. */
export type ImageryStatus = "waiting" | "live" | "off" | "unreachable";
/**
 * A decoded tile: what the view draws, a small RGBA sample of it to judge whether it is blank, and whether any of it is
 * transparent (the edge of the coverage), so the relief has to show through there.
 */
export type DecodedImagery<Image> = { image: Image; sample: ArrayLike<number>; partial: boolean };
/** A tile's imagery, and whether the relief must be drawn under it. */
export type ImageryTile<Image> = { image: Image; partial: boolean };
export type ImageryDecoder<Image> = (tile: Blob) => Promise<DecodedImagery<Image>>;

/** The deepest level the imagery is published at (the relay refuses deeper). */
export const IMAGERY_MAX_ZOOM = 16;
/** The side of the sample a decoded tile is judged blank from, pixels. */
export const BLANK_SAMPLE_PIXELS = 16;

/**
 * Whether a tile has no imagery in it: every sampled pixel either near white (the service's blank filler) or fully
 * transparent (an edge tile with nothing on this side of the coverage). Real imagery, even snow or cloud, has texture
 * at this scale; a filler tile is flat white.
 */
export function isBlankTile(rgba: ArrayLike<number>, floor = 248): boolean {
  for (let i = 0; i < rgba.length; i += 4) {
    if (rgba[i + 3] === 0) continue;
    if (rgba[i] < floor || rgba[i + 1] < floor || rgba[i + 2] < floor) return false;
  }
  return rgba.length > 0;
}

/**
 * The browser's decoder: the tile as an ImageBitmap, a 16 × 16 downsample of it to judge, and, for a PNG, whether any
 * pixel is transparent. That is read at full size, because a sliver of coast would vanish in the downsample and then be
 * drawn black. A JPEG has no transparency.
 */
export const browserImageryDecoder = (): ImageryDecoder<ImageBitmap> => {
  let scratch: CanvasRenderingContext2D | null = null;
  let full: CanvasRenderingContext2D | null = null;
  return async tile => {
    const image = await createImageBitmap(tile);
    scratch ??= Object.assign(document.createElement("canvas"), { width: BLANK_SAMPLE_PIXELS, height: BLANK_SAMPLE_PIXELS })
      .getContext("2d", { willReadFrequently: true })!;
    // Cleared first: a transparent tile drawn over the last one would be judged by both.
    scratch.clearRect(0, 0, BLANK_SAMPLE_PIXELS, BLANK_SAMPLE_PIXELS);
    scratch.drawImage(image, 0, 0, TILE_PIXELS, TILE_PIXELS, 0, 0, BLANK_SAMPLE_PIXELS, BLANK_SAMPLE_PIXELS);
    let partial = false;
    if (tile.type === "image/png") {
      full ??= Object.assign(document.createElement("canvas"), { width: TILE_PIXELS, height: TILE_PIXELS })
        .getContext("2d", { willReadFrequently: true })!;
      full.clearRect(0, 0, TILE_PIXELS, TILE_PIXELS);
      full.drawImage(image, 0, 0);
      const rgba = full.getImageData(0, 0, TILE_PIXELS, TILE_PIXELS).data;
      for (let i = 3; i < rgba.length && !partial; i += 4) partial = rgba[i] < 255;
    }
    return { image, sample: scratch.getImageData(0, 0, BLANK_SAMPLE_PIXELS, BLANK_SAMPLE_PIXELS).data, partial };
  };
};

export class GroundImagery<Image = ImageBitmap> {
  private readonly listeners = new Set<() => void>();
  private current: ImageryStatus = "waiting";
  private readonly source: ImagerySource;
  private readonly decode: ImageryDecoder<Image>;

  constructor(source: ImagerySource, decode: ImageryDecoder<Image>) {
    this.source = source;
    this.decode = decode;
  }

  get status(): ImageryStatus { return this.current; }
  /**
   * Whether any tile drawn came from Esri World Imagery (the relay's worldwide fallback outside the USGS coverage,
   * DEC-151), so the view credits Esri while its imagery is on screen, as Esri's terms require.
   */
  get usesEsri(): boolean { return this.esri; }
  private esri = false;

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /**
   * A tile's imagery, or null where there is none (none published, blank, off, or failing): draw relief there. A partial
   * tile is drawn over its relief.
   */
  async load(z: number, x: number, y: number): Promise<ImageryTile<Image> | null> {
    if (z > IMAGERY_MAX_ZOOM || this.current === "off") return null;
    let response: Response;
    try {
      response = await this.source(z, x, y);
    } catch {
      this.report("unreachable");
      return null;
    }
    if (!response.ok) {
      const body = response.status === 404 ? await response.json().catch(() => null) as { code?: string } | null : null;
      // Outside the coverage is not a failure; an installation that turned imagery off says so, and that stays.
      if (body?.code === "imagery_relay_disabled") this.report("off");
      else if (response.status !== 404) this.report("unreachable");
      // "None here" is the source answering, so it ends an earlier failure. Over Canada every tile is a 404, and
      // without this one dropped tile kept the view saying the source was unreachable for the rest of the flight.
      else if (this.current === "unreachable") this.report("waiting");
      return null;
    }
    let decoded: DecodedImagery<Image>;
    try {
      decoded = await this.decode(await response.blob());
    } catch {
      return null;
    }
    this.report("live");
    if (isBlankTile(decoded.sample)) return null;
    if (!this.esri && response.headers.get("x-imagery-source") === "esri") {
      this.esri = true;
      for (const listener of this.listeners) listener();
    }
    return { image: decoded.image, partial: decoded.partial };
  }

  private report(status: ImageryStatus) {
    if (this.current === status || this.current === "off") return;
    this.current = status;
    for (const listener of this.listeners) listener();
  }
}
