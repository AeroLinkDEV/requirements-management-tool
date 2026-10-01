import { API_ORIGIN } from "../apiOrigin";
import type { ImagerySource } from "./groundImagery";
import type { TerrainSource } from "./terrainTiles";

/** This server's relay (FmsBenchTerrainEndpoints.cs): the only place the browser may fetch terrain from. */
export const relayTerrain: TerrainSource = (z, x, y) =>
  fetch(`${API_ORIGIN}/api/fms-bench/terrain/${z}/${x}/${y}`, { credentials: "include" });

/** USGS imagery first; the server's optional Esri fallback after a decoded blank USGS tile. */
export const relayImagery: ImagerySource = (z, x, y, fallback = false) =>
  fetch(`${API_ORIGIN}/api/fms-bench/imagery/${z}/${x}/${y}${fallback ? "?fallback=true" : ""}`, { credentials: "include" });
