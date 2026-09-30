import { API_ORIGIN } from "../apiOrigin";
import type { ImagerySource } from "./groundImagery";
import type { TerrainSource } from "./terrainTiles";

/** This server's relay (FmsBenchTerrainEndpoints.cs): the only place the browser may fetch terrain from. */
export const relayTerrain: TerrainSource = (z, x, y) =>
  fetch(`${API_ORIGIN}/api/fms-bench/terrain/${z}/${x}/${y}`, { credentials: "include" });

/** This server's imagery relay (FmsBenchImageryEndpoints.cs): the USGS orthoimagery, the United States only. */
export const relayImagery: ImagerySource = (z, x, y) =>
  fetch(`${API_ORIGIN}/api/fms-bench/imagery/${z}/${x}/${y}`, { credentials: "include" });
