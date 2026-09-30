import { crc32q } from "./gps";
import type { LatLon } from "./fmsModel";
import { WMM2025_DATABASE } from "./wmm2025";

export type AngleReference = "MAG" | "TRUE";
export type MagvarDatabase = { format: "aerolink-magvar-v1"; name: string; epoch: number; released: string; coefficients: string; crc: string };
type Coefficient = { n: number; m: number; g: number; h: number; dg: number; dh: number };
export type MagneticField = { north: number; east: number; down: number; horizontal: number; total: number; inclination: number; declination: number };
const rad = Math.PI / 180, degree = 1 / rad;
const index = (n: number, m: number) => n * (n + 1) / 2 + m;
export const normalizeAngle = (angle: number) => ((angle % 360) + 360) % 360;
export const polarRegion = (position: LatLon) => position.lat > 73 || position.lat < -60;

/** U.S. Government public-domain material: port of NOAA GeomagnetismLibrary.c's degree-12 WMM algorithm.
 * Uses WGS84 geodetic latitude and height ABOVE THE ELLIPSOID in km, not barometric/MSL height.
 * https://www.ncei.noaa.gov/products/world-magnetic-model/software-coefficients-linux
 * Gauss recursion, Schmidt normalization, geocentric summation and rotation follow the NOAA library.
 */
function field(coefficients: readonly Coefficient[], epoch: number, position: LatLon, heightKm: number, date: Date): MagneticField | null {
  if (![position.lat, position.lon, heightKm, date.getTime()].every(Number.isFinite) || Math.abs(position.lat) > 90 || Math.abs(position.lon) > 360 || heightKm < -1 || heightKm > 850) return null;
  const year = date.getUTCFullYear(), start = Date.UTC(year, 0, 1), end = Date.UTC(year + 1, 0, 1);
  const elapsed = year + (date.getTime() - start) / (end - start) - epoch;
  const latitude = position.lat * rad, longitude = position.lon * rad;
  const a = 6378.137, b = 6356.7523142, eccentricity = 1 - b * b / (a * a);
  const curvature = a / Math.sqrt(1 - eccentricity * Math.sin(latitude) ** 2);
  const xp = (curvature + heightKm) * Math.cos(latitude), zp = (curvature * (1 - eccentricity) + heightKm) * Math.sin(latitude);
  const radius = Math.hypot(xp, zp), geocentric = Math.asin(zp / radius), x = Math.sin(geocentric), z = Math.sqrt((1 - x) * (1 + x));
  const p = new Float64Array(91), dp = new Float64Array(91), schmidt = new Float64Array(91);
  p[0] = 1; schmidt[0] = 1;
  for (let n = 1; n <= 12; n++) {
    schmidt[index(n, 0)] = schmidt[index(n - 1, 0)] * (2 * n - 1) / n;
    for (let m = 0; m <= n; m++) {
      const at = index(n, m), previous = index(n - 1, m);
      if (n === m) {
        p[at] = z * p[previous - 1]; dp[at] = z * dp[previous - 1] + x * p[previous - 1];
      } else if (n === 1 || m > n - 2) {
        p[at] = x * p[previous]; dp[at] = x * dp[previous] - z * p[previous];
      } else {
        const earlier = index(n - 2, m), k = ((n - 1) ** 2 - m * m) / ((2 * n - 1) * (2 * n - 3));
        p[at] = x * p[previous] - k * p[earlier]; dp[at] = x * dp[previous] - z * p[previous] - k * dp[earlier];
      }
      if (m > 0) schmidt[at] = schmidt[at - 1] * Math.sqrt((n - m + 1) * (m === 1 ? 2 : 1) / (n + m));
    }
  }
  for (let at = 1; at < 91; at++) { p[at] *= schmidt[at]; dp[at] *= -schmidt[at]; }
  let north = 0, east = 0, down = 0;
  for (const c of coefficients) {
    const at = index(c.n, c.m), power = (6371.2 / radius) ** (c.n + 2);
    const g = c.g + elapsed * c.dg, h = c.h + elapsed * c.dh;
    const cosine = Math.cos(c.m * longitude), sine = Math.sin(c.m * longitude), component = g * cosine + h * sine;
    north -= power * component * dp[at];
    down -= power * component * (c.n + 1) * p[at];
    east += power * (g * sine - h * cosine) * c.m * p[at];
  }
  if (Math.abs(Math.cos(geocentric)) > 1e-10) east /= Math.cos(geocentric);
  else {
    east = 0;
    const pole = new Float64Array(13); pole[0] = 1;
    let normalization = 1;
    for (let n = 1; n <= 12; n++) {
      const next = normalization * (2 * n - 1) / n;
      const scale = next * Math.sqrt(2 * n / (n + 1)); normalization = next;
      pole[n] = n === 1 ? pole[n - 1] : x * pole[n - 1] - ((n - 1) ** 2 - 1) / ((2 * n - 1) * (2 * n - 3)) * pole[n - 2];
      const c = coefficients.find(coefficient => coefficient.n === n && coefficient.m === 1)!;
      east += (6371.2 / radius) ** (n + 2) * ((c.g + elapsed * c.dg) * Math.sin(longitude) - (c.h + elapsed * c.dh) * Math.cos(longitude)) * pole[n] * scale;
    }
  }
  const rotation = geocentric - latitude, geodeticNorth = north * Math.cos(rotation) - down * Math.sin(rotation);
  down = north * Math.sin(rotation) + down * Math.cos(rotation); north = geodeticNorth;
  const horizontal = Math.hypot(north, east);
  return { north, east, down, horizontal, total: Math.hypot(horizontal, down), inclination: Math.atan2(down, horizontal) * degree, declination: Math.atan2(east, north) * degree };
}

/** The consumed table's integrity; replacement is the same boundary used by the bench loader. */
export class MagvarModel {
  private coefficients: Coefficient[] = [];
  private table: MagvarDatabase = WMM2025_DATABASE;
  private checked = false;
  private cached: { key: string; value: MagneticField | null } | null = null;
  constructor() { this.load(WMM2025_DATABASE); }
  load(candidate: unknown): boolean {
    if (!candidate || typeof candidate !== "object") return false;
    const value = candidate as Partial<MagvarDatabase>;
    if (value.format !== "aerolink-magvar-v1" || typeof value.name !== "string" || !Number.isFinite(value.epoch) || typeof value.released !== "string" || typeof value.coefficients !== "string" || typeof value.crc !== "string") return false;
    this.table = { ...value } as MagvarDatabase;
    this.cached = null;
    const payload = [value.format, value.name, value.epoch, value.released, value.coefficients];
    const crc = crc32q(new TextEncoder().encode(JSON.stringify(payload))).toString(16).toUpperCase().padStart(8, "0");
    const lines = value.coefficients.trim().split(/\r?\n/), header = lines.shift()!.trim().split(/\s+/);
    const parsed: Coefficient[] = [];
    for (const line of lines) {
      if (line.trim().startsWith("9999")) continue;
      const values = line.trim().split(/\s+/).map(Number);
      if (values.length !== 6 || !values.every(Number.isFinite)) { parsed.length = 0; break; }
      const [n, m, g, h, dg, dh] = values; parsed.push({ n, m, g, h, dg, dh });
    }
    const identities = new Set(parsed.map(c => `${c.n}/${c.m}`));
    this.checked = value.crc === crc && Number(header[0]) === value.epoch && header[1]?.replace("-", "") === value.name
      && parsed.length === 90 && identities.size === 90 && parsed.every(c => Number.isInteger(c.n) && Number.isInteger(c.m) && c.n >= 1 && c.n <= 12 && c.m >= 0 && c.m <= c.n);
    this.coefficients = this.checked ? parsed : [];
    return true;
  }
  get database(): Readonly<MagvarDatabase> { return { ...this.table }; }
  get valid() { return this.checked; }
  outOfDate(date: Date) { return date.getTime() > Date.UTC(this.table.epoch + 5, 0, 1); }
  withinEpoch(date: Date) { return date.getTime() >= Date.UTC(this.table.epoch, 0, 1) && !this.outOfDate(date); }
  field(position: LatLon, heightAboveEllipsoidKm: number, date: Date) {
    if (!this.checked) return null;
    const key = `${position.lat}/${position.lon}/${heightAboveEllipsoidKm}/${date.getTime()}`;
    if (this.cached?.key !== key) this.cached = { key, value: field(this.coefficients, this.table.epoch, position, heightAboveEllipsoidKm, date) };
    return this.cached.value ? { ...this.cached.value } : null;
  }
}
