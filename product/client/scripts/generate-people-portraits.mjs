// Generates the FMS showcase portrait set (#913).
//
// Deterministic, identity-safe, repository-owned synthetic portraits for every seeded
// AeroLink account, written one file per exact username into product/client/public/people/
// together with a manifest the client registry and the coverage diagnostics read.
//
// Identity safety: the generator emits exactly one portrait file per exact username and the
// manifest maps that username to that file. No portrait is ever chosen by username prefix,
// display-name similarity, or render-time inference; the assignment is the committed file set
// itself. The four curated AI portraits are copied through unchanged.
//
// Determinism: the same username always produces the same image (FNV-1a hash drives palette,
// hair, and accessory parameters), so re-running the generator never churns the repository.
//
// Usage: node product/client/scripts/generate-people-portraits.mjs
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync, existsSync, copyFileSync, readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const clientRoot = join(here, "..");
const outDir = join(clientRoot, "public", "people");
const curatedDir = join(clientRoot, "src", "assets", "people");
// The manifest is bundled with the client (src/) so portrait resolution needs no runtime fetch;
// the images themselves are static files under public/people/ fetched only when rendered.
const manifestPath = join(clientRoot, "src", "people-manifest.json");

const SIZE = 128;
// Owner intent (#913): thumbnail-quality UI assets, "roughly <=150-200 KB per portrait where
// practical", never high-resolution photography. The four curated AI portraits sit just over
// 200 KB (largest 206,640 bytes), so the hard guard is set at 256 KB: comfortably above the
// practical aim, far below any multi-megabyte source image slipping in.
const MAX_BYTES = 256 * 1024;

// The authoritative account roster. Kept in step with IdentityService.cs (People + GeneratedPeople
// + admin); a C# test in AeroLink.Api.Tests asserts this list still covers the live FMS membership.
// Each entry carries the seeded display name and the account's display role (the base-role
// vocabulary the product surfaces).
const curated = {
  "admin": { name: "AeroLink Administrator", role: "Administrator" },
  "engineer.demo": { name: "Sean Engineer", role: "Engineer" },
  "systems.author": { name: "Systems Requirements Author", role: "System Engineer" },
  "software.author": { name: "Software Requirements Author", role: "Software Engineer" },
  "systems.reviewer": { name: "Systems Engineer", role: "System Engineer" },
  "assurance.reviewer": { name: "Development Assurance Reviewer", role: "Software Quality Assurance" },
  "lead.reviewer": { name: "Maya Patel", role: "Reviewer" },
  "software.lead": { name: "Rina Shah", role: "Software Engineering Lead" },
  "systems.lead": { name: "Systems Engineering Lead", role: "System Engineer" },
  "engineering.manager": { name: "Engineering Manager", role: "Engineering Manager" },
  "manager.reviewer": { name: "Olivia Chen", role: "Program Manager" },
  "program.manager": { name: "Olivia Chen", role: "Program Manager" },
  "release.manager": { name: "Daniel Reyes", role: "Configuration Manager" },
  "cm.fms": { name: "Configuration Manager", role: "Configuration Manager" },
  "test.author": { name: "Verification Author", role: "Test Engineer" },
  "test.engineer": { name: "Ethan Brooks", role: "Test Engineer" },
  "airworthiness.lead": { name: "Priya Raman", role: "Airworthiness" },
  "quality.analyst": { name: "Marcus Hale", role: "Software Quality Assurance" },
  "project.lead": { name: "Nadia Okoro", role: "Project Engineer" },
};
const curatedPortraitSources = {
  "lead.reviewer": join(curatedDir, "maya-patel.png"),
  "test.engineer": join(curatedDir, "ethan-brooks.png"),
  "manager.reviewer": join(curatedDir, "olivia-chen.png"),
  "program.manager": join(curatedDir, "olivia-chen.png"),
  "release.manager": join(curatedDir, "daniel-reyes.png"),
};
const firstNames = ["Avery", "Blake", "Cameron", "Casey", "Devon", "Emerson", "Finley", "Harper", "Jordan", "Kai", "Logan", "Morgan", "Parker", "Quinn", "Reese", "Riley", "Rowan", "Sage", "Sawyer", "Taylor", "Alex", "Jamie", "Robin"];
const lastNames = ["Anderson", "Bennett", "Campbell", "Chen", "Clarke", "Dubois", "Evans", "Foster", "Garcia", "Gupta", "Harris", "Ibrahim", "Johnson", "Kim", "Lewis", "Martin", "Nguyen", "Patel", "Robinson", "Wilson"];
const groupRoles = {
  "system.engineer": "System Engineer",
  "software.engineer": "Software Engineer",
  "verification.engineer": "Test Engineer",
  "systems.lead": "Reviewer",
  "software.lead": "Reviewer",
  "engineering.manager": "Program Manager",
  "configuration.specialist": "Configuration Manager",
};
const roster = { ...curated };
for (let index = 0; index < 184; index++) {
  const name = `${firstNames[index % firstNames.length]} ${lastNames[(index * 7) % lastNames.length]}`;
  let group;
  if (index < 42) group = "system.engineer";
  else if (index < 104) group = "software.engineer";
  else if (index < 138) group = "verification.engineer";
  else if (index < 160) group = index % 2 === 0 ? "systems.lead" : "software.lead";
  else if (index < 174) group = "engineering.manager";
  else group = "configuration.specialist";
  roster[`${group}.${String(index + 1).padStart(3, "0")}`] = { name, role: groupRoles[group] };
}

const hash = (text) => {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h;
};

const backgrounds = ["#dff0ee", "#e3ecf7", "#f2e9dd", "#e8e4f3", "#e2f0e2", "#f4e6e0", "#e0edf4", "#efe8f0"];
const skins = ["#f2c9a4", "#e8b48b", "#d19a6b", "#b97b50", "#8d5a34", "#6f4325"];
const hairs = ["#20242c", "#3d2f24", "#5c4330", "#7a5c3a", "#1d1d20", "#4a4a4f", "#8c5a2b", "#b8b8bc"];
const shirts = ["#2f6f8f", "#3c7a5a", "#7a5c9c", "#a05a48", "#48639c", "#8f7040", "#4c8f7a", "#8f4c66", "#5a6d7a", "#6d5a8f"];

const pick = (h, mod) => h % mod;
const pickFrom = (h, list, salt) => list[(Math.floor(h / 7) + salt * 13) % list.length];

function drawAvatar(pixels, size, username) {
  const h = hash(username);
  const background = pickFrom(h, backgrounds, 0);
  const skin = pickFrom(h, skins, 1);
  const hairColor = pickFrom(h, hairs, 2);
  const shirt = pickFrom(h, shirts, 3);
  const hairStyle = pick(h, 6);
  const glasses = pick(Math.floor(h / 11), 4) === 0;
  const beard = pick(Math.floor(h / 13), 5) === 0;
  const hairVariant = pick(Math.floor(h / 17), 2);

  // All geometry below is authored in a 128-unit design space and scaled to the render size,
  // so supersampling preserves the composition exactly.
  const s = size / 128;
  const cx = 64 * s;
  const setPixel = (x, y, color) => {
    if (x < 0 || y < 0 || x >= size || y >= size) return;
    const at = (y * size + x) * 4;
    pixels[at] = parseInt(color.slice(1, 3), 16);
    pixels[at + 1] = parseInt(color.slice(3, 5), 16);
    pixels[at + 2] = parseInt(color.slice(5, 7), 16);
    pixels[at + 3] = 255;
  };
  const shade = (color, factor) => {
    const r = parseInt(color.slice(1, 3), 16), g = parseInt(color.slice(3, 5), 16), b = parseInt(color.slice(5, 7), 16);
    const mix = (channel) => Math.max(0, Math.min(255, Math.round(channel * factor)));
    return `#${[mix(r), mix(g), mix(b)].map(v => v.toString(16).padStart(2, "0")).join("")}`;
  };
  const ellipse = (ecx, ecy, rx, ry, color) => {
    ecx *= s; ecy *= s; rx *= s; ry *= s;
    for (let y = Math.floor(ecy - ry); y <= Math.ceil(ecy + ry); y++) {
      for (let x = Math.floor(ecx - rx); x <= Math.ceil(ecx + rx); x++) {
        const dx = (x - ecx) / rx, dy = (y - ecy) / ry;
        if (dx * dx + dy * dy <= 1) setPixel(x, y, color);
      }
    }
  };
  const rect = (x0, y0, w, h2, color) => {
    x0 *= s; y0 *= s; w *= s; h2 *= s;
    for (let y = Math.round(y0); y < Math.round(y0 + h2); y++) for (let x = Math.round(x0); x < Math.round(x0 + w); x++) setPixel(x, y, color);
  };

  rect(0, 0, 128, 128, background);
  // Shoulders and torso: the body enters from the bottom edge and fills the lower third.
  ellipse(64, 148, 44, 46, shirt);
  ellipse(64, 156, 44, 46, shade(shirt, 0.85));
  // Neck.
  rect(57, 82, 14, 14, shade(skin, 0.9));
  // Head.
  ellipse(64, 58, 26, 30, skin);
  ellipse(38, 58, 3.5, 7, shade(skin, 0.88));
  ellipse(90, 58, 3.5, 7, shade(skin, 0.88));

  // Hair.
  if (hairStyle === 0) ellipse(64, 40, 27, 15, hairColor); // short cap
  else if (hairStyle === 1) { // side part
    ellipse(60, 39, 26, 14, hairColor);
    rect(76, 36, 12, 18, hairColor);
  } else if (hairStyle === 2) { // curly
    for (let i = -3; i <= 3; i++) ellipse(64 + i * 8, 36 - Math.abs(i) * 1.5, 7.5, 7, hairColor);
    rect(42, 34, 44, 9, hairColor);
  } else if (hairStyle === 3) { // long, down the sides
    ellipse(64, 40, 28, 15, hairColor);
    rect(34, 40, 11, 44, hairColor);
    rect(83, 40, 11, 44, hairColor);
  } else if (hairStyle === 4) { // top knot
    ellipse(64, 40, 26, 14, hairColor);
    ellipse(64, 20, 8, 8, shade(hairColor, 0.8));
  } // style 5: close-cropped / none

  // Eyes, brows, mouth.
  ellipse(55, 58, 2.4, 2.6, "#20242c");
  ellipse(73, 58, 2.4, 2.6, "#20242c");
  rect(50, 51, 7.5, 2, shade(hairColor, 0.9));
  rect(70.5, 51, 7.5, 2, shade(hairColor, 0.9));
  rect(58, 72, 12, 2.2, shade(skin, 0.62));
  if (beard) ellipse(64, 76, 15, 10, shade(hairColor, 0.85));
  if (glasses) {
    ellipse(55, 58, 6, 5.2, "rgba(32,36,44,0.14)");
    ellipse(73, 58, 6, 5.2, "rgba(32,36,44,0.14)");
    for (let x = Math.round(59 * s); x <= Math.round(69 * s); x++) setPixel(x, Math.round(58 * s), "#20242c");
    for (let x = Math.round(43 * s); x <= Math.round(49 * s); x++) setPixel(x, Math.round(57 * s), "#20242c");
    for (let x = Math.round(79 * s); x <= Math.round(85 * s); x++) setPixel(x, Math.round(57 * s), "#20242c");
  }
  if (hairVariant === 1) ellipse(46, 52, 3.2, 2, shade(skin, 0.8)); // subtle cheek variation
}

// Supersampled render for smooth edges: draw at 2x, box-downsample to SIZE.
function render(username) {
  const ss = SIZE * 2;
  const buf = Buffer.alloc(ss * ss * 4);
  drawAvatar(buf, ss, username);
  const out = Buffer.alloc(SIZE * SIZE * 4);
  for (let y = 0; y < SIZE; y++) {
    for (let x = 0; x < SIZE; x++) {
      for (let c = 0; c < 4; c++) {
        const sum =
          buf[((y * 2) * ss + (x * 2)) * 4 + c] + buf[((y * 2) * ss + (x * 2 + 1)) * 4 + c] +
          buf[((y * 2 + 1) * ss + (x * 2)) * 4 + c] + buf[((y * 2 + 1) * ss + (x * 2 + 1)) * 4 + c];
        out[(y * SIZE + x) * 4 + c] = sum >> 2;
      }
    }
  }
  return out;
}

const crcTable = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();
const crc32 = (buffer) => {
  let c = 0xffffffff;
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
};
function encodePng(rgba, size) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0;
    rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

mkdirSync(outDir, { recursive: true });
// The manifest mirrors IdentityService.cs's directory: per exact username, the portrait file and the
// seeded display name. It is the client's explicit identity mapping — nothing is inferred at render time.
const manifest = { version: 1, people: {} };
let oversized = [];
for (const [username, person] of Object.entries(roster)) {
  const source = curatedPortraitSources[username];
  const file = `${username}.png`;
  const target = join(outDir, file);
  if (source) {
    if (!existsSync(source)) throw new Error(`curated portrait missing: ${source}`);
    copyFileSync(source, target);
  } else {
    writeFileSync(target, encodePng(render(username), SIZE));
  }
  const bytes = readFileSync(target);
  if (bytes.length > MAX_BYTES) oversized.push({ file, bytes: bytes.length });
  manifest.people[username] = { file: `/people/${file}`, name: person.name, role: person.role };
}
if (oversized.length) throw new Error(`portraits over ${MAX_BYTES} bytes: ${JSON.stringify(oversized)}`);
writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
console.log(`wrote ${Object.keys(roster).length} portraits + manifest to ${outDir}`);
