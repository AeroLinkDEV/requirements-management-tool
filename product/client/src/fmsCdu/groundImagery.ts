import { TILE_PIXELS } from "./outTheWindow";

/**
 * Aerial imagery for the out-the-window view's ground, through the server's relay (FmsBenchImageryEndpoints.cs): the
 * USGS National Map orthoimagery (US federal public-domain data, 6 inches to 1 metre, the United States only). Where
 * there is none, the view draws its elevation relief instead, tile by tile: the relay answers 404 outside the
 * coverage, and at low zoom levels near its edge the service fills a tile with blank white, which counts as none too.
 */

/** Where imagery tiles come from: a JPEG (or PNG), a 404 where there is none, or a failure. */
export type ImagerySource = (z: number, x: number, y: number) => Promise<Response>;
/** Nothing yet; tiles arriving; turned off on this installation; or not answering. */
export type ImageryStatus = "waiting" | "live" | "off" | "unreachable";
/** A decoded tile: what the view draws, and a small RGBA sample of it to judge whether it is blank. */
export type DecodedImagery<Image> = { image: Image; sample: ArrayLike<number> };
export type ImageryDecoder<Image> = (tile: Blob) => Promise<DecodedImagery<Image>>;

/** The deepest level the imagery is published at (the relay refuses deeper). */
export const IMAGERY_MAX_ZOOM = 16;
/** The side of the sample a decoded tile is judged blank from, pixels. */
export const BLANK_SAMPLE_PIXELS = 16;

/**
 * Whether a tile is the service's blank filler: every sampled pixel near white. Real imagery, even snow or cloud, has
 * texture at this scale; a filler tile is flat white.
 */
export function isBlankTile(rgba: ArrayLike<number>, floor = 248): boolean {
  for (let i = 0; i < rgba.length; i += 4) if (rgba[i] < floor || rgba[i + 1] < floor || rgba[i + 2] < floor) return false;
  return rgba.length > 0;
}

/** The browser's decoder: the tile as an ImageBitmap, and a 16 × 16 downsample of it to judge. */
export const browserImageryDecoder = (): ImageryDecoder<ImageBitmap> => {
  let scratch: CanvasRenderingContext2D | null = null;
  return async tile => {
    const image = await createImageBitmap(tile);
    scratch ??= Object.assign(document.createElement("canvas"), { width: BLANK_SAMPLE_PIXELS, height: BLANK_SAMPLE_PIXELS })
      .getContext("2d", { willReadFrequently: true })!;
    scratch.drawImage(image, 0, 0, TILE_PIXELS, TILE_PIXELS, 0, 0, BLANK_SAMPLE_PIXELS, BLANK_SAMPLE_PIXELS);
    return { image, sample: scratch.getImageData(0, 0, BLANK_SAMPLE_PIXELS, BLANK_SAMPLE_PIXELS).data };
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

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /** A tile's image, or null where there is no imagery (none published, blank, off, or failing): draw relief there. */
  async load(z: number, x: number, y: number): Promise<Image | null> {
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
      return null;
    }
    let decoded: DecodedImagery<Image>;
    try {
      decoded = await this.decode(await response.blob());
    } catch {
      return null;
    }
    this.report("live");
    return isBlankTile(decoded.sample) ? null : decoded.image;
  }

  private report(status: ImageryStatus) {
    if (this.current === status || this.current === "off") return;
    this.current = status;
    for (const listener of this.listeners) listener();
  }
}
