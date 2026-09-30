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
 * them. The output is deterministic: the same code gives the same bytes (scripts/build-helicopter-model.mjs writes
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

/** The body, the main rotor and the tail fan, each a list of solids. */
export function helicopterParts(): { body: Part[]; mainRotor: Part[]; tailRotor: Part[] } {
  const body: Part[] = [
    // Cabin and nose: a long ellipsoid, the nose rounded forward, the cabin floor flat enough to sit on the skids.
    { mesh: ellipsoid([0.75, 0, -0.35], [2.65, 0.78, 0.95]), material: "body" },
    { mesh: ellipsoid([2.05, 0, -0.1], [1.3, 0.7, 0.72]), material: "glass" },
    // The engine and gearbox fairing on top, behind the mast.
    { mesh: ellipsoid([-0.5, 0, 0.62], [1.9, 0.55, 0.42]), material: "body" },
    { mesh: tube([0, 0, 0.7], [0, 0, 1.5], 0.12), material: "metal" },
    // The tail boom, tapering aft to the shroud.
    { mesh: tube([-2.0, 0, 0.35], [-5.85, 0, 0.5], 0.42, 0.18), material: "body" },
    // The shrouded fan's housing and fin.
    { mesh: ring(TAIL_ROTOR_CENTRE, 0.55, 0.14, 0.18), material: "body" },
    { mesh: box([-6.7, 0, 1.45], [0.8, 0.1, 1.3]), material: "body" },
    // The horizontal stabiliser with its end plates.
    { mesh: box([-4.8, 0, 0.5], [0.55, 2.5, 0.07]), material: "body" },
    { mesh: box([-4.8, 1.25, 0.62], [0.6, 0.05, 0.45]), material: "body" },
    { mesh: box([-4.8, -1.25, 0.62], [0.6, 0.05, 0.45]), material: "body" },
    // Skids: two tubes along x, turned up at the front, on two cross tubes.
    { mesh: tube([2.3, 1.0, -1.45], [-1.6, 1.0, -1.45], 0.05), material: "metal" },
    { mesh: tube([-1.6, 1.0, -1.45], [-1.8, 1.0, -1.35], 0.05), material: "metal" },
    { mesh: tube([2.3, 1.0, -1.45], [2.65, 1.0, -1.2], 0.05), material: "metal" },
    { mesh: tube([2.3, -1.0, -1.45], [-1.6, -1.0, -1.45], 0.05), material: "metal" },
    { mesh: tube([-1.6, -1.0, -1.45], [-1.8, -1.0, -1.35], 0.05), material: "metal" },
    { mesh: tube([2.3, -1.0, -1.45], [2.65, -1.0, -1.2], 0.05), material: "metal" },
    { mesh: tube([1.3, 1.0, -1.45], [1.3, -1.0, -1.45], 0.045), material: "metal" },
    { mesh: tube([-0.9, 1.0, -1.45], [-0.9, -1.0, -1.45], 0.045), material: "metal" },
    { mesh: tube([1.3, 0.55, -0.9], [1.3, 1.0, -1.45], 0.045), material: "metal" },
    { mesh: tube([1.3, -0.55, -0.9], [1.3, -1.0, -1.45], 0.045), material: "metal" },
    { mesh: tube([-0.9, 0.55, -0.9], [-0.9, 1.0, -1.45], 0.045), material: "metal" },
    { mesh: tube([-0.9, -0.55, -0.9], [-0.9, -1.0, -1.45], 0.045), material: "metal" },
  ];
  // The main rotor, in its node's frame (the node sits at MAIN_ROTOR_CENTRE): a hub and four blades, and a faint disc
  // for the blur of the turning blades.
  const mainRotor: Part[] = [
    { mesh: ellipsoid([0, 0, 0], [0.32, 0.32, 0.14]), material: "metal" },
    ...[0, 1, 2, 3].map(k => {
      const a = (k * Math.PI) / 2, c = Math.cos(a), s = Math.sin(a), mid = (MAIN_ROTOR_RADIUS + 0.35) / 2;
      const blade = box([0, 0, 0], [MAIN_ROTOR_RADIUS - 0.35, 0.3, 0.04]);
      // Rotated about z by a, then moved out along the blade.
      const positions = blade.positions.slice(), normals = blade.normals.slice();
      for (let i = 0; i < positions.length; i += 3) {
        const x = positions[i] + mid, y = positions[i + 1];
        positions[i] = x * c - y * s; positions[i + 1] = x * s + y * c;
        const nx = normals[i], ny = normals[i + 1];
        normals[i] = nx * c - ny * s; normals[i + 1] = nx * s + ny * c;
      }
      return { mesh: { positions, normals, indices: blade.indices }, material: "rotor" as const };
    }),
    { mesh: disc([0, 0, 0.01], MAIN_ROTOR_RADIUS), material: "blur" },
  ];
  // The tail fan, in its node's frame (centred in the shroud, turning about y): ten blades.
  const tailRotor: Part[] = Array.from({ length: 10 }, (_, k) => {
    const a = (k * 2 * Math.PI) / 10, c = Math.cos(a), s = Math.sin(a);
    const blade = box([0.25, 0, 0], [0.4, 0.03, 0.08]);
    const positions = blade.positions.slice(), normals = blade.normals.slice();
    for (let i = 0; i < positions.length; i += 3) {
      const x = positions[i], z = positions[i + 2];
      positions[i] = x * c - z * s; positions[i + 2] = x * s + z * c;
      const nx = normals[i], nz = normals[i + 2];
      normals[i] = nx * c - nz * s; normals[i + 2] = nx * s + nz * c;
    }
    return { mesh: { positions, normals, indices: blade.indices }, material: "rotor" as const };
  });
  return { body, mainRotor, tailRotor };
}

/** Merges solids of one material into one mesh. */
function merge(parts: Part[]): Map<MaterialId, Mesh> {
  const out = new Map<MaterialId, Mesh>();
  for (const part of parts) {
    const into = out.get(part.material) ?? { positions: [], normals: [], indices: [] };
    const base = into.positions.length / 3;
    into.positions.push(...part.mesh.positions);
    into.normals.push(...part.mesh.normals);
    into.indices.push(...part.mesh.indices.map(i => i + base));
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
