import type { LatLon } from "./fmsModel";
import type { StoredRoute } from "./navData";

/**
 * The user database (plan E5; M300 11-23…11-31): the user waypoints the crew stores, among them one made from a MARK
 * ON TOP (NEW USER WPT), and the user routes saved from RTE. The CMA-9000 keeps it in its own memory; the bench keeps
 * it behind a storage interface, so a native store can replace the browser one.
 *
 * - Scope: the signed-in AeroLink user and the aircraft profile. Browser storage is shared by everyone who uses the
 *   same browser, so the origin alone does not keep users apart; the key names both.
 * - Export: a versioned document (schema and version), which import reads back.
 * - Import is atomic: a document that is malformed in any part writes nothing. A user waypoint or route whose ident is
 *   already used for something different is a collision: the import writes nothing and names each collision, so
 *   nothing is overwritten silently. An entry identical to one already stored is not a collision.
 *
 * Fixed user waypoints only in v1; the moving ones (M300 11-25) are not stored. At most 460 user waypoints (M300 11-24).
 */

export const USER_WAYPOINT_CAPACITY = 460;
export const USER_DATABASE_SCHEMA = "aerolink.fms.user-database";
export const USER_DATABASE_VERSION = 1;

export type UserWaypoint = { ident: string; position: LatLon; type: "FIXED" };
export type UserDatabase = { waypoints: UserWaypoint[]; routes: StoredRoute[] };
export type UserScope = { userId: string; profileId: string };

/** Where the user database is kept: a string per scope. */
export interface UserDatabaseStore {
  read(scope: UserScope): string | null;
  write(scope: UserScope, text: string): void;
}

export const EMPTY_USER_DATABASE: UserDatabase = { waypoints: [], routes: [] };

const key = (scope: UserScope) => `aerolink.fms.user-database.v${USER_DATABASE_VERSION}/${encodeURIComponent(scope.userId)}/${encodeURIComponent(scope.profileId)}`;

/** The browser store: one localStorage entry per user and profile. A storage that throws reads as empty. */
export function browserUserDatabaseStore(storage: Pick<Storage, "getItem" | "setItem">): UserDatabaseStore {
  return {
    read: scope => { try { return storage.getItem(key(scope)); } catch { return null; } },
    write: (scope, text) => storage.setItem(key(scope), text),
  };
}

/** A store in memory, for tests and for a bench with no signed-in user. */
export function memoryUserDatabaseStore(): UserDatabaseStore & { entries: Map<string, string> } {
  const entries = new Map<string, string>();
  return { entries, read: scope => entries.get(key(scope)) ?? null, write: (scope, text) => { entries.set(key(scope), text); } };
}

/** The versioned document: what the store keeps and what export gives. */
export function serializeUserDatabase(scope: UserScope, data: UserDatabase, exported: Date): string {
  return JSON.stringify({
    schema: USER_DATABASE_SCHEMA, version: USER_DATABASE_VERSION, userId: scope.userId, profileId: scope.profileId,
    exported: exported.toISOString(), waypoints: data.waypoints, routes: data.routes,
  }, null, 2);
}

const IDENT = /^[A-Z0-9]{1,5}$/;
const ROUTE_NAME = /^[A-Z0-9]{1,10}$/;
const onGlobe = (p: unknown): p is LatLon => {
  const q = p as LatLon;
  return typeof q === "object" && q !== null && Number.isFinite(q.lat) && Number.isFinite(q.lon) && Math.abs(q.lat) <= 90 && Math.abs(q.lon) <= 180;
};

/**
 * Reads a document back, or gives every reason it is not a valid user database. Nothing is kept from a document with
 * any error: the caller writes all of it or none.
 */
export function parseUserDatabase(text: string): { data: UserDatabase; scope: UserScope } | { errors: string[] } {
  let raw: unknown;
  try { raw = JSON.parse(text); } catch { return { errors: ["not JSON"] }; }
  const doc = raw as Record<string, unknown>;
  const errors: string[] = [];
  if (typeof doc !== "object" || doc === null) return { errors: ["not a user database document"] };
  if (doc.schema !== USER_DATABASE_SCHEMA) errors.push(`schema is not ${USER_DATABASE_SCHEMA}`);
  if (doc.version !== USER_DATABASE_VERSION) errors.push(`version ${String(doc.version)} is not supported (expected ${USER_DATABASE_VERSION})`);
  const waypoints = Array.isArray(doc.waypoints) ? doc.waypoints : (errors.push("waypoints is not a list"), []);
  const routes = Array.isArray(doc.routes) ? doc.routes : (errors.push("routes is not a list"), []);
  const seen = new Set<string>();
  const outWaypoints: UserWaypoint[] = [];
  waypoints.forEach((w: Record<string, unknown>, i: number) => {
    const ident = typeof w?.ident === "string" ? w.ident : "";
    if (!IDENT.test(ident)) { errors.push(`waypoint ${i + 1}: ident ${JSON.stringify(w?.ident)} is not 1-5 letters or digits`); return; }
    if (seen.has(ident)) { errors.push(`waypoint ${i + 1}: ${ident} appears twice`); return; }
    if (!onGlobe(w.position)) { errors.push(`waypoint ${ident}: position is not a latitude and longitude`); return; }
    if (w.type !== "FIXED") { errors.push(`waypoint ${ident}: type ${JSON.stringify(w.type)} is not FIXED`); return; }
    seen.add(ident);
    outWaypoints.push({ ident, position: { lat: w.position.lat, lon: w.position.lon }, type: "FIXED" });
  });
  if (outWaypoints.length > USER_WAYPOINT_CAPACITY) errors.push(`${outWaypoints.length} waypoints is more than the ${USER_WAYPOINT_CAPACITY} the database holds`);
  const names = new Set<string>();
  const outRoutes: StoredRoute[] = [];
  routes.forEach((r: Record<string, unknown>, i: number) => {
    const name = typeof r?.name === "string" ? r.name : "";
    if (!ROUTE_NAME.test(name)) { errors.push(`route ${i + 1}: name ${JSON.stringify(r?.name)} is not 1-10 letters or digits`); return; }
    if (names.has(name)) { errors.push(`route ${i + 1}: ${name} appears twice`); return; }
    if (typeof r.origin !== "string" || typeof r.dest !== "string") { errors.push(`route ${name}: origin and destination are required`); return; }
    if (!Array.isArray(r.legs) || !r.legs.every((l: Record<string, unknown>) => typeof l?.ident === "string" && IDENT.test(l.ident as string))) {
      errors.push(`route ${name}: legs must each name a waypoint`); return;
    }
    names.add(name);
    outRoutes.push({
      name, origin: r.origin, dest: r.dest,
      legs: (r.legs as Record<string, unknown>[]).map(l => ({ ident: l.ident as string, ...(typeof l.via === "string" ? { via: l.via } : {}), ...(typeof l.altitude === "string" ? { altitude: l.altitude } : {}) })),
    });
  });
  if (errors.length) return { errors };
  return { data: { waypoints: outWaypoints, routes: outRoutes }, scope: { userId: String(doc.userId ?? ""), profileId: String(doc.profileId ?? "") } };
}

const samePlace = (a: LatLon, b: LatLon) => Math.abs(a.lat - b.lat) < 1e-9 && Math.abs(a.lon - b.lon) < 1e-9;
const sameRoute = (a: StoredRoute, b: StoredRoute) => JSON.stringify(a) === JSON.stringify(b);

/**
 * What an import would make of the stored database: the merged database, what it adds, and the collisions. With any
 * collision the caller writes nothing.
 */
export function mergeUserDatabase(current: UserDatabase, incoming: UserDatabase) {
  const collisions: string[] = [];
  const waypoints = [...current.waypoints], routes = [...current.routes];
  let addedWaypoints = 0, addedRoutes = 0;
  for (const w of incoming.waypoints) {
    const existing = current.waypoints.find(c => c.ident === w.ident);
    if (!existing) { waypoints.push(w); addedWaypoints += 1; }
    else if (!samePlace(existing.position, w.position)) collisions.push(`user waypoint ${w.ident} is already stored at another position`);
  }
  for (const r of incoming.routes) {
    const existing = current.routes.find(c => c.name === r.name);
    if (!existing) { routes.push(r); addedRoutes += 1; }
    else if (!sameRoute(existing, r)) collisions.push(`user route ${r.name} is already stored with different contents`);
  }
  if (waypoints.length > USER_WAYPOINT_CAPACITY) collisions.push(`the import would store ${waypoints.length} waypoints, more than ${USER_WAYPOINT_CAPACITY}`);
  return { data: { waypoints, routes }, addedWaypoints, addedRoutes, collisions };
}
