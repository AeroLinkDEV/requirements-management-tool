import { useEffect, useRef, useState } from "react";
import type { AircraftData } from "./efis";
import { SVS_ZOOM, renderSyntheticVision, type SvsView } from "./syntheticVision";
import type { TerrainTiles } from "./terrainTiles";

const FT = 0.3048;
/** Side of the square drawn, in PFD units: it covers the attitude window at any bank. */
const SIDE = 352;

/**
 * The synthetic vision picture behind the PFD's attitude indicator, as an SVG layer. It is drawn unrolled and square
 * about the boresight; the PFD rotates it with the bank, the way it rotates its sky and ground. `pitchPx` is the PFD's
 * pitch scale, which fixes the focal length so the terrain is conformal with the pitch ladder.
 */
export function SyntheticVisionLayer({ air, tiles, cx, cy, pitchPx }: { air: AircraftData; tiles: TerrainTiles; cx: number; cy: number; pitchPx: number }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  // Redrawn when a tile arrives as well as when the aircraft moves; arrivals within a frame are drawn once.
  const [arrivals, setArrivals] = useState(0);
  useEffect(() => {
    let frame = 0;
    const unsubscribe = tiles.subscribe(() => {
      if (!frame) frame = requestAnimationFrame(() => { frame = 0; setArrivals(count => count + 1); });
    });
    return () => { unsubscribe(); cancelAnimationFrame(frame); };
  }, [tiles]);

  const { lat, lon } = air.position;
  useEffect(() => {
    const pen = canvas.current?.getContext("2d");
    if (!pen) return;
    const view: SvsView = { width: SIDE, height: SIDE, centreX: SIDE / 2, centreY: SIDE / 2, focal: pitchPx / Math.tan(Math.PI / 180) };
    const image = pen.createImageData(SIDE, SIDE);
    renderSyntheticVision(image.data, view, { lat, lon, altitude: air.physicalAltitude * FT, heading: air.heading, pitch: air.pitch },
      (pointLat, pointLon) => tiles.heightAt(pointLat, pointLon, SVS_ZOOM));
    pen.putImageData(image, 0, 0);
  }, [lat, lon, air.physicalAltitude, air.heading, air.pitch, tiles, pitchPx, arrivals]);

  return (
    <foreignObject x={cx - SIDE / 2} y={cy - SIDE / 2} width={SIDE} height={SIDE} data-testid="pfd-svs">
      <canvas ref={canvas} width={SIDE} height={SIDE} style={{ width: "100%", height: "100%", display: "block" }} />
    </foreignObject>
  );
}
