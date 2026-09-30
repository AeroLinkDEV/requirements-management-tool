/**
 * The chase view's helicopter as a glTF 2.0 binary (GLB), generated here from simple solids. It is the bench's own
 * model of a generic light twin-engine helicopter in the class of the H135 (about 10.2 m main rotor, a shrouded tail
 * fan, skids, a four-blade main rotor), built for the FMS Test Bench (Sean, 29 September: no open-licence Airbus
 * model could be downloaded without signing in, so the bench builds its own). It is not an Airbus Helicopters asset or
 * a model of a real aircraft's shape: no livery, no logos, no trademarks.
 *
 * Axes: +x forward, +y left, +z up, metres, the origin at the main rotor mast's foot (the same frame as the
 * boxes-and-ellipsoids fallback, outTheWindow.ts AIRCRAFT_PARTS); the scene loads it with those axes declared. The
 * main rotor and the tail fan are separate nodes ("main_rotor" about +z, "tail_rotor" about +y), so the view can turn
 * them. The output is deterministic: the same code gives the same bytes (buildHelicopterModel.mjs writes
 * public/fms-cdu/models/helicopter-light-twin.glb, and a test checks the committed file against it).
 */

type Vec3 = [number, number, number];
type Mesh = { positions: number[]; normals: number[]; indices: number[] };
type Part = { mesh: Mesh; material: MaterialId };
type MaterialId = "body" | "glass" | "metal" | "rotor" | "blur";

const MATERIALS: Record<MaterialId, { colour: [number, number, number, number]; metallic: number; roughness: number }> = {
  body: { colour: [0.92, 0.93, 0.94, 1], metallic: 0.1, roughness: 0.45 },
  glass: { colour: [0.08, 0.12, 0.16, 1], metallic: 0.2, roughness: 0.1 },
  metal: { colour: [0.35, 0.37, 0.4, 1], metallic: 0.8, roughness: 0.4 },
  rotor: { colour: [0.12, 0.12, 0.13, 1], metallic: 0.3, roughness: 0.6 },
  blur: { colour: [0.2, 0.2, 0.22, 0.18], metallic: 0, roughness: 1 },
};

/** A UV ellipsoid about a centre, with radii along x, y, z. */
function ellipsoid(centre: Vec3, radii: Vec3, rings = 12, sectors = 18): Mesh {
  const positions: number[] = [], normals: number[] = [], indices: number[] = [];
  for (let r = 0; r <= rings; r += 1) {
    const phi = (Math.PI * r) / rings;
    for (let s = 0; s <= sectors; s += 1) {
      const theta = (2 * Math.PI * s) / sectors;
      const nx = Math.sin(phi) * Math.cos(theta), ny = Math.sin(phi) * Math.sin(theta), nz = Math.cos(phi);
      positions.push(centre[0] + radii[0] * nx, centre[1] + radii[1] * ny, centre[2] + radii[2] * nz);
      const n = [nx / radii[0], ny / radii[1], nz / radii[2]], len = Math.hypot(...n) || 1;
      normals.push(n[0] / len, n[1] / len, n[2] / len);
    }
  }
  for (let r = 0; r < rings; r += 1) for (let s = 0; s < sectors; s += 1) {
    const a = r * (sectors + 1) + s, b = a + sectors + 1;
    indices.push(a, b, a + 1, b, b + 1, a + 1);
  }
  return { positions, normals, indices };
}

/** An axis-aligned box about a centre, with full sizes along x, y, z. */
function box(centre: Vec3, size: Vec3): Mesh {
  const positions: number[] = [], normals: number[] = [], indices: number[] = [];
  const h = size.map(v => v / 2) as Vec3;
  const faces: [Vec3, Vec3, Vec3][] = [
    [[1, 0, 0], [0, 1, 0], [0, 0, 1]], [[-1, 0, 0], [0, 0, 1], [0, 1, 0]],
    [[0, 1, 0], [0, 0, 1], [1, 0, 0]], [[0, -1, 0], [1, 0, 0], [0, 0, 1]],
    [[0, 0, 1], [1, 0, 0], [0, 1, 0]], [[0, 0, -1], [0, 1, 0], [1, 0, 0]],
  ];
  for (const [n, u, v] of faces) {
    const base = positions.length / 3;
    for (const [su, sv] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
      for (let k = 0; k < 3; k += 1) positions.push(centre[k] + h[k] * (n[k] + su * u[k] + sv * v[k]));
      normals.push(...n);
    }
    indices.push(base, base + 1, base + 2, base, base + 2, base + 3);
  }
  return { positions, normals, indices };
}

/** A cylinder (or a cone, with two radii) from one point to another. */
function tube(from: Vec3, to: Vec3, r0: number, r1 = r0, sectors = 12): Mesh {
  const axis = [to[0] - from[0], to[1] - from[1], to[2] - from[2]], length = Math.hypot(...axis);
  const w = axis.map(v => v / length) as Vec3;
  const helper: Vec3 = Math.abs(w[2]) < 0.9 ? [0, 0, 1] : [1, 0, 0];
  const u = normalise(cross(w, helper)), v = cross(w, u);
  const positions: number[] = [], normals: number[] = [], indices: number[] = [];
  for (let s = 0; s <= sectors; s += 1) {
    const t = (2 * Math.PI * s) / sectors, c = Math.cos(t), si = Math.sin(t);
    const n = [u[0] * c + v[0] * si, u[1] * c + v[1] * si, u[2] * c + v[2] * si];
    for (const [end, r] of [[from, r0], [to, r1]] as const) {
      positions.push(end[0] + n[0] * r, end[1] + n[1] * r, end[2] + n[2] * r);
      normals.push(...n);
    }
  }
  for (let s = 0; s < sectors; s += 1) { const a = s * 2; indices.push(a, a + 2, a + 1, a + 1, a + 2, a + 3); }
  return { positions, normals, indices };
}

/** A ring (a torus) about a centre, its axis along y: the tail fan's shroud. */
function ring(centre: Vec3, radius: number, thickness: number, width: number, sectors = 24): Mesh {
  const positions: number[] = [], normals: number[] = [], indices: number[] = [];
  const minor = 8;
  for (let s = 0; s <= sectors; s += 1) {
    const t = (2 * Math.PI * s) / sectors;
    for (let m = 0; m <= minor; m += 1) {
      const p = (2 * Math.PI * m) / minor;
      const rr = radius + thickness * Math.cos(p);
      positions.push(centre[0] + rr * Math.cos(t), centre[1] + width * Math.sin(p), centre[2] + rr * Math.sin(t));
      normals.push(Math.cos(p) * Math.cos(t), Math.sin(p), Math.cos(p) * Math.sin(t));
    }
  }
  for (let s = 0; s < sectors; s += 1) for (let m = 0; m < minor; m += 1) {
    const a = s * (minor + 1) + m, b = a + minor + 1;
    indices.push(a, b, a + 1, b, b + 1, a + 1);
  }
  return { positions, normals, indices };
}

/** A flat disc about a centre, normal along z: the main rotor's translucent blur. */
function disc(centre: Vec3, radius: number, sectors = 32): Mesh {
  const positions = [...centre], normals = [0, 0, 1], indices: number[] = [];
  for (let s = 0; s <= sectors; s += 1) {
    const t = (2 * Math.PI * s) / sectors;
    positions.push(centre[0] + radius * Math.cos(t), centre[1] + radius * Math.sin(t), centre[2]);
    normals.push(0, 0, 1);
  }
  for (let s = 1; s <= sectors; s += 1) indices.push(0, s, s + 1, 0, s + 1, s);
  return { positions, normals, indices };
}

const cross = (a: Vec3, b: Vec3): Vec3 => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const normalise = (a: Vec3): Vec3 => { const l = Math.hypot(...a); return [a[0] / l, a[1] / l, a[2] / l]; };

/** The main rotor's centre above the mast foot, and the tail fan's centre, metres. */
export const MAIN_ROTOR_CENTRE: Vec3 = [0, 0, 1.55];
export const TAIL_ROTOR_CENTRE: Vec3 = [-6.55, 0, 0.55];
export const MAIN_ROTOR_RADIUS = 5.1;

/** Linear interpolation through (x, value) keys sorted by x, smoothed between keys (smoothstep). */
function keyed(keys: [number, number][], x: number): number {
  if (x <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i += 1) {
    const [x0, v0] = keys[i - 1], [x1, v1] = keys[i];
    if (x <= x1) { const t = (x - x0) / (x1 - x0), s = t * t * (3 - 2 * t); return v0 + (v1 - v0) * s; }
  }
  return keys[keys.length - 1][1];
}

/**
 * A lofted body along x: at each station a superellipse cross-section with its half-width, its top and bottom, and an
 * exponent (2 an ellipse, larger boxier). The angle range lets a loft cover only part of the section (a window band).
 * Normals are computed from the surface, so it shades smoothly.
 */
function loft(stations: number, around: number, xAt: (i: number) => number, section: (x: number) => { halfWidth: number; top: number; bottom: number; exponent: number },
  angles: [number, number] = [0, 2 * Math.PI], offset = 0): Mesh {
  const grid: Vec3[][] = [];
  for (let i = 0; i <= stations; i += 1) {
    const x = xAt(i), s = section(x), cz = (s.top + s.bottom) / 2, hz = (s.top - s.bottom) / 2, e = 2 / s.exponent;
    const row: Vec3[] = [];
    for (let j = 0; j <= around; j += 1) {
      const t = angles[0] + ((angles[1] - angles[0]) * j) / around, c = Math.cos(t), si = Math.sin(t);
      const y = Math.sign(c) * Math.abs(c) ** e * (s.halfWidth + offset), z = cz + Math.sign(si) * Math.abs(si) ** e * (hz + offset);
      row.push([x, y, z]);
    }
    grid.push(row);
  }
  return gridMesh(grid);
}

/** A mesh from a grid of points (rows along the length, columns around), with averaged face normals. */
function gridMesh(grid: Vec3[][]): Mesh {
  const rows = grid.length, cols = grid[0].length;
  const positions: number[] = [], normals: number[] = [], indices: number[] = [];
  const acc = grid.map(row => row.map(() => [0, 0, 0] as Vec3));
  for (let i = 0; i < rows - 1; i += 1) for (let j = 0; j < cols - 1; j += 1) {
    const a = grid[i][j], b = grid[i + 1][j], c = grid[i][j + 1];
    const n = cross([b[0] - a[0], b[1] - a[1], b[2] - a[2]], [c[0] - a[0], c[1] - a[1], c[2] - a[2]]);
    for (const [ii, jj] of [[i, j], [i + 1, j], [i, j + 1], [i + 1, j + 1]]) for (let k = 0; k < 3; k += 1) acc[ii][jj][k] += n[k];
  }
  for (let i = 0; i < rows; i += 1) for (let j = 0; j < cols; j += 1) {
    positions.push(...grid[i][j]);
    const n = acc[i][j], l = Math.hypot(...n) || 1;
    normals.push(n[0] / l, n[1] / l, n[2] / l);
  }
  for (let i = 0; i < rows - 1; i += 1) for (let j = 0; j < cols - 1; j += 1) {
    const a = i * cols + j, b = a + cols;
    indices.push(a, b, a + 1, b, b + 1, a + 1);
  }
  return { positions, normals, indices };
}

/** A symmetric airfoil section (NACA 00xx) of unit chord, as points around from the trailing edge. */
function airfoil(thickness: number, points = 14): [number, number][] {
  const half = (x: number) => 5 * thickness * (0.2969 * Math.sqrt(x) - 0.126 * x - 0.3516 * x * x + 0.2843 * x ** 3 - 0.1036 * x ** 4);
  const xs = Array.from({ length: points + 1 }, (_, i) => (1 - Math.cos((Math.PI * i) / points)) / 2);
  return [...xs.slice().reverse().map(x => [x, half(x)] as [number, number]), ...xs.slice(1).map(x => [x, -half(x)] as [number, number])];
}

/**
 * A wing along a span axis: an airfoil section (chord along `chordAxis`, thickness along the third axis) swept from
 * `root` along `span` with a chord and twist that vary along it. Used for rotor blades, the fin and the stabiliser.
 */
function wing(root: Vec3, span: Vec3, chordAxis: Vec3, steps: number, chordAt: (t: number) => number, thickness: number, twistAt: (t: number) => number = () => 0, leadingEdgeAt = 0.25): Mesh {
  const length = Math.hypot(...span), s = span.map(v => v / length) as Vec3;
  const cAxis = normalise(chordAxis), tAxis = normalise(cross(s, cAxis));
  const section = airfoil(thickness);
  const grid: Vec3[][] = [];
  for (let i = 0; i <= steps; i += 1) {
    const t = i / steps, chord = chordAt(t), twist = twistAt(t), ct = Math.cos(twist), st = Math.sin(twist);
    const centre: Vec3 = [root[0] + span[0] * t, root[1] + span[1] * t, root[2] + span[2] * t];
    grid.push(section.map(([cx, cy]) => {
      const u = (leadingEdgeAt - cx) * chord, v = cy * chord;
      const a = u * ct - v * st, b = u * st + v * ct;
      return [centre[0] + cAxis[0] * a + tAxis[0] * b, centre[1] + cAxis[1] * a + tAxis[1] * b, centre[2] + cAxis[2] * a + tAxis[2] * b] as Vec3;
    }));
  }
  return gridMesh(grid);
}

/** A tube along a path of points (a bent skid or cross tube). */
function path(points: Vec3[], radius: number, sectors = 16): Mesh {
  const grid: Vec3[][] = points.map((p, i) => {
    const a = points[Math.max(0, i - 1)], b = points[Math.min(points.length - 1, i + 1)];
    const w = normalise([b[0] - a[0], b[1] - a[1], b[2] - a[2]]);
    const helper: Vec3 = Math.abs(w[2]) < 0.9 ? [0, 0, 1] : [1, 0, 0];
    const u = normalise(cross(w, helper)), v = cross(w, u);
    return Array.from({ length: sectors + 1 }, (_, k) => {
      const t = (2 * Math.PI * k) / sectors, c = Math.cos(t), s = Math.sin(t);
      return [p[0] + (u[0] * c + v[0] * s) * radius, p[1] + (u[1] * c + v[1] * s) * radius, p[2] + (u[2] * c + v[2] * s) * radius] as Vec3;
    });
  });
  return gridMesh(grid);
}

/** Points along an arc from a to b bowing out by `bow` toward `towards` (a skid's bent cross tube). */
function bent(a: Vec3, b: Vec3, mid: Vec3, steps = 10): Vec3[] {
  return Array.from({ length: steps + 1 }, (_, i) => {
    const t = i / steps, u = 1 - t;
    return [u * u * a[0] + 2 * u * t * mid[0] + t * t * b[0], u * u * a[1] + 2 * u * t * mid[1] + t * t * b[1], u * u * a[2] + 2 * u * t * mid[2] + t * t * b[2]] as Vec3;
  });
}

/** Rotates a mesh about z (or y) by an angle, about the origin. */
function rotated(mesh: Mesh, angle: number, axis: "z" | "y"): Mesh {
  const c = Math.cos(angle), s = Math.sin(angle), p = mesh.positions.slice(), n = mesh.normals.slice();
  const [i0, i1] = axis === "z" ? [0, 1] : [0, 2];
  for (let i = 0; i < p.length; i += 3) {
    const x = p[i + i0], y = p[i + i1]; p[i + i0] = x * c - y * s; p[i + i1] = x * s + y * c;
    const nx = n[i + i0], ny = n[i + i1]; n[i + i0] = nx * c - ny * s; n[i + i1] = nx * s + ny * c;
  }
  return { positions: p, normals: n, indices: mesh.indices };
}

/** The fuselage's cross-section at x: the rounded nose, the cabin, the tapering aft body and the tail boom. */
function fuselage(x: number) {
  return {
    halfWidth: keyed([[-6.0, 0.16], [-2.4, 0.3], [-1.3, 0.72], [-0.2, 0.8], [1.6, 0.8], [2.7, 0.66], [3.25, 0.34], [3.42, 0.05]], x),
    top: keyed([[-6.0, 0.62], [-2.4, 0.66], [-1.3, 0.7], [0.4, 0.74], [1.6, 0.66], [2.6, 0.2], [3.2, -0.2], [3.42, -0.42]], x),
    bottom: keyed([[-6.0, 0.3], [-2.4, 0.18], [-1.3, -0.95], [-0.6, -1.2], [2.0, -1.2], [2.9, -0.95], [3.3, -0.7], [3.42, -0.5]], x),
    exponent: keyed([[-6.0, 2], [-2.4, 2.2], [-1.3, 2.8], [2.2, 2.8], [3.42, 2.2]], x),
  };
}
const FUSELAGE_NOSE = 3.42, FUSELAGE_TAIL = -6.05;

/** The body, the main rotor and the tail fan. */
export function helicopterParts(): { body: Part[]; mainRotor: Part[]; tailRotor: Part[] } {
  const stations = 120, around = 64;
  const along = (from: number, to: number, n: number) => (i: number) => from + ((to - from) * i) / n;
  const body: Part[] = [
    // The fuselage in one smooth loft from the nose to the end of the boom.
    { mesh: loft(stations, around, along(FUSELAGE_TAIL, FUSELAGE_NOSE, stations), fuselage), material: "body" },
    // The windscreen and chin windows: the forward upper surface, a few centimetres proud of the body.
    { mesh: loft(40, 40, along(1.55, 3.36, 40), fuselage, [-0.15 * Math.PI, 1.15 * Math.PI], 0.012), material: "glass" },
    // The cabin side windows and doors' glazing, a band each side.
    { mesh: loft(36, 10, along(-0.9, 1.5, 36), fuselage, [0.08 * Math.PI, 0.34 * Math.PI], 0.01), material: "glass" },
    { mesh: loft(36, 10, along(-0.9, 1.5, 36), fuselage, [0.66 * Math.PI, 0.92 * Math.PI], 0.01), material: "glass" },
    // The engine and gearbox fairing on the roof, with its intakes and two exhausts.
    { mesh: loft(60, 40, along(-2.35, 0.95, 60), x => ({
      halfWidth: keyed([[-2.35, 0.2], [-1.6, 0.52], [0.2, 0.55], [0.95, 0.2]], x),
      top: keyed([[-2.35, 0.7], [-1.6, 1.12], [0.3, 1.18], [0.95, 0.76]], x),
      bottom: 0.55, exponent: 2.6,
    })), material: "body" },
    { mesh: ellipsoid([0.35, 0.5, 1.0], [0.28, 0.07, 0.12], 10, 16), material: "metal" },
    { mesh: ellipsoid([0.35, -0.5, 1.0], [0.28, 0.07, 0.12], 10, 16), material: "metal" },
    { mesh: tube([-1.9, 0.3, 1.02], [-2.4, 0.42, 1.08], 0.1, 0.12, 20), material: "metal" },
    { mesh: tube([-1.9, -0.3, 1.02], [-2.4, -0.42, 1.08], 0.1, 0.12, 20), material: "metal" },
    // The mast and its fairing.
    { mesh: tube([0, 0, 1.1], [0, 0, 1.52], 0.13, 0.1, 24), material: "metal" },
    // The shrouded fan's housing: a thick ring, faired into the boom, with the fin above and a ventral fin below.
    { mesh: ring(TAIL_ROTOR_CENTRE, 0.52, 0.16, 0.2, 48), material: "body" },
    { mesh: wing([-6.2, 0, 1.0], [-0.55, 0, 1.2], [1, 0, 0], 20, t => 0.9 - 0.35 * t, 0.12), material: "body" },
    { mesh: wing([-6.35, 0, 0.1], [-0.15, 0, -0.45], [1, 0, 0], 8, t => 0.55 - 0.2 * t, 0.12), material: "body" },
    // The horizontal stabiliser (an airfoil), with end plates.
    { mesh: wing([-4.75, -1.3, 0.52], [0, 2.6, 0], [1, 0, 0], 30, () => 0.55, 0.12), material: "body" },
    { mesh: wing([-4.8, 1.3, 0.3], [0, 0, 0.55], [1, 0, 0], 8, t => 0.6 - 0.15 * t, 0.1), material: "body" },
    { mesh: wing([-4.8, -1.3, 0.3], [0, 0, 0.55], [1, 0, 0], 8, t => 0.6 - 0.15 * t, 0.1), material: "body" },
    // Skids: tubes turned up at the front, on two bent cross tubes, with a step each side.
    ...[1, -1].flatMap(side => [
      { mesh: path([[-1.75, side * 1.05, -1.38], [-1.6, side * 1.05, -1.45], ...bent([-1.4, side * 1.05, -1.47], [2.2, side * 1.05, -1.47], [0.4, side * 1.05, -1.47], 6), ...bent([2.2, side * 1.05, -1.47], [2.75, side * 1.05, -1.1], [2.6, side * 1.05, -1.48], 8).slice(1)], 0.045), material: "metal" as const },
      { mesh: box([0.5, side * 0.98, -1.18], [0.5, 0.18, 0.03]), material: "metal" as const },
    ]),
    ...[1.25, -0.85].map(x => ({ mesh: path(bent([x, 1.05, -1.47], [x, -1.05, -1.47], [x, 0, -0.6], 16), 0.04), material: "metal" as const })),
    // The landing light under the nose and the antennas on the boom and belly.
    { mesh: ellipsoid([2.9, 0, -0.88], [0.1, 0.1, 0.05], 8, 16), material: "glass" },
    { mesh: wing([-3.4, 0, 0.55], [0, 0, 0.3], [1, 0, 0], 4, t => 0.18 - 0.1 * t, 0.1), material: "metal" },
    { mesh: wing([0.9, 0, -1.2], [0, 0, -0.18], [1, 0, 0], 4, t => 0.12 - 0.06 * t, 0.1), material: "metal" },
  ];
  // The main rotor, in its node's frame (at MAIN_ROTOR_CENTRE): a hub with its star plate, four tapered, twisted
  // airfoil blades, the pitch links, and a faint disc for the blur of the turning blades.
  const blade = wing([0, -0.35, 0], [0, -(MAIN_ROTOR_RADIUS - 0.35), 0], [1, 0, 0], 48, t => (t > 0.94 ? 0.3 - 0.12 * (t - 0.94) / 0.06 : 0.3), 0.12, t => 0.14 - 0.14 * t);
  const mainRotor: Part[] = [
    { mesh: tube([0, 0, -0.12], [0, 0, 0.1], 0.3, 0.26, 32), material: "metal" },
    { mesh: ellipsoid([0, 0, 0.12], [0.2, 0.2, 0.12], 12, 24), material: "metal" },
    ...[0, 1, 2, 3].flatMap(k => [
      { mesh: rotated(blade, (k * Math.PI) / 2, "z"), material: "rotor" as const },
      { mesh: rotated(tube([0.08, -0.22, -0.3], [0.1, -0.3, -0.02], 0.02, 0.02, 8), (k * Math.PI) / 2, "z"), material: "metal" as const },
    ]),
    { mesh: tube([0, 0, -0.34], [0, 0, -0.28], 0.28, 0.28, 32), material: "metal" },
    { mesh: disc([0, 0, 0.01], MAIN_ROTOR_RADIUS, 64), material: "blur" },
  ];
  // The tail fan, in its node's frame (centred in the shroud, turning about y): a hub and ten blades, unevenly spaced
  // as shrouded fans are to spread their noise.
  const spacing = [0, 33, 72, 108, 144, 180, 213, 252, 288, 324];
  const fanBlade = wing([0.08, 0, 0], [0.44, 0, 0], [0, 0, 1], 10, () => 0.09, 0.12, () => 0.35);
  const tailRotor: Part[] = [
    { mesh: tube([0, -0.07, 0], [0, 0.07, 0], 0.09, 0.09, 24), material: "metal" },
    ...spacing.map(deg => ({ mesh: rotated(fanBlade, (deg * Math.PI) / 180, "y"), material: "rotor" as const })),
  ];
  return { body, mainRotor, tailRotor };
}

/** Merges solids of one material into one mesh. */
function merge(parts: Part[]): Map<MaterialId, Mesh> {
  const out = new Map<MaterialId, Mesh>();
  for (const part of parts) {
    const into = out.get(part.material) ?? { positions: [], normals: [], indices: [] };
    const base = into.positions.length / 3;
    for (const v of part.mesh.positions) into.positions.push(v);
    for (const v of part.mesh.normals) into.normals.push(v);
    for (const i of part.mesh.indices) into.indices.push(i + base);
    out.set(part.material, into);
  }
  return out;
}

/** The model as GLB bytes: glTF 2.0, one buffer, a mesh per node (a primitive per material). */
export function buildHelicopterGlb(): Uint8Array {
  const { body, mainRotor, tailRotor } = helicopterParts();
  const materialIds = Object.keys(MATERIALS) as MaterialId[];
  const chunks: Uint8Array[] = [];
  let length = 0;
  const bufferViews: object[] = [], accessors: object[] = [];
  const add = (bytes: Uint8Array, target: number) => {
    const pad = (4 - (length % 4)) % 4;
    if (pad) { chunks.push(new Uint8Array(pad)); length += pad; }
    bufferViews.push({ buffer: 0, byteOffset: length, byteLength: bytes.byteLength, target });
    chunks.push(bytes);
    length += bytes.byteLength;
    return bufferViews.length - 1;
  };
  const meshOf = (parts: Part[], name: string) => {
    const primitives: object[] = [];
    for (const [material, mesh] of merge(parts)) {
      const positions = new Float32Array(mesh.positions), normals = new Float32Array(mesh.normals);
      const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
      for (let i = 0; i < positions.length; i += 3) for (let k = 0; k < 3; k += 1) { min[k] = Math.min(min[k], positions[i + k]); max[k] = Math.max(max[k], positions[i + k]); }
      const count = positions.length / 3;
      const indices = count > 65535 ? new Uint32Array(mesh.indices) : new Uint16Array(mesh.indices);
      const pv = add(new Uint8Array(positions.buffer), 34962), nv = add(new Uint8Array(normals.buffer), 34962);
      const iv = add(new Uint8Array(indices.buffer, indices.byteOffset, indices.byteLength), 34963);
      accessors.push({ bufferView: pv, componentType: 5126, count, type: "VEC3", min: min.map(v => Math.fround(v)), max: max.map(v => Math.fround(v)) });
      const position = accessors.length - 1;
      accessors.push({ bufferView: nv, componentType: 5126, count, type: "VEC3" });
      const normal = accessors.length - 1;
      accessors.push({ bufferView: iv, componentType: indices instanceof Uint32Array ? 5125 : 5123, count: indices.length, type: "SCALAR" });
      primitives.push({ attributes: { POSITION: position, NORMAL: normal }, indices: accessors.length - 1, material: materialIds.indexOf(material) });
    }
    return { name, primitives };
  };
  const meshes = [meshOf(body, "body"), meshOf(mainRotor, "main_rotor"), meshOf(tailRotor, "tail_rotor")];
  const json = {
    asset: { version: "2.0", generator: "AeroLink FMS Test Bench helicopterModel.ts", copyright: "AeroLink project; an original generic model, no trademarks" },
    scene: 0,
    scenes: [{ nodes: [0] }],
    nodes: [
      { name: "helicopter", mesh: 0, children: [1, 2] },
      { name: "main_rotor", mesh: 1, translation: MAIN_ROTOR_CENTRE },
      { name: "tail_rotor", mesh: 2, translation: TAIL_ROTOR_CENTRE },
    ],
    meshes,
    materials: materialIds.map(id => {
      const m = MATERIALS[id];
      return { name: id, pbrMetallicRoughness: { baseColorFactor: m.colour, metallicFactor: m.metallic, roughnessFactor: m.roughness }, ...(m.colour[3] < 1 ? { alphaMode: "BLEND", doubleSided: true } : {}) };
    }),
    accessors,
    bufferViews,
    buffers: [{ byteLength: 0 }],
  };
  const tail = (4 - (length % 4)) % 4;
  if (tail) { chunks.push(new Uint8Array(tail)); length += tail; }
  json.buffers[0].byteLength = length;
  let jsonBytes = new TextEncoder().encode(JSON.stringify(json));
  const jsonPad = (4 - (jsonBytes.length % 4)) % 4;
  if (jsonPad) { const padded = new Uint8Array(jsonBytes.length + jsonPad).fill(0x20); padded.set(jsonBytes); jsonBytes = padded; }
  const total = 12 + 8 + jsonBytes.length + 8 + length;
  const out = new Uint8Array(total);
  const view = new DataView(out.buffer);
  view.setUint32(0, 0x46546c67, true); view.setUint32(4, 2, true); view.setUint32(8, total, true);
  view.setUint32(12, jsonBytes.length, true); view.setUint32(16, 0x4e4f534a, true);
  out.set(jsonBytes, 20);
  let at = 20 + jsonBytes.length;
  view.setUint32(at, length, true); view.setUint32(at + 4, 0x004e4942, true);
  at += 8;
  for (const chunk of chunks) { out.set(chunk, at); at += chunk.byteLength; }
  return out;
}
