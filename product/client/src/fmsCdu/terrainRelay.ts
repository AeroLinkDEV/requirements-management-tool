import { API_ORIGIN } from "../apiOrigin";
import type { TerrainSource } from "./terrainTiles";

/** This server's relay (FmsBenchTerrainEndpoints.cs): the only place the browser may fetch terrain from. */
export const relayTerrain: TerrainSource = (z, x, y) =>
  fetch(`${API_ORIGIN}/api/fms-bench/terrain/${z}/${x}/${y}`, { credentials: "include" });
