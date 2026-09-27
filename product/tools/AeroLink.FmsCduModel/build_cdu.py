"""Builds and renders the CMA-9000 CDU faceplate headlessly.

    blender --background --factory-startup --python build_cdu.py -- <out_dir> [--samples N] [--ppmm 8]

Writes panel.webp (keys up), pressed.webp (every key pressed; the client clips it to a pressed key's
rectangle), layout.json (every key, annunciator and the screen's active area in image pixels) and
cdu.blend. Legends are not rendered: the client draws them per hardware variation.
"""
import json
import math
import os
import sys

import bpy  # noqa: provided by Blender

sys.dont_write_bytecode = True  # keep __pycache__ out of the repository
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cdu_layout as L  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(args[0] if args else "out")
SAMPLES = int(args[args.index("--samples") + 1]) if "--samples" in args else 96
PPMM = float(args[args.index("--ppmm") + 1]) if "--ppmm" in args else 8.0
os.makedirs(OUT, exist_ok=True)

MM = 0.001  # scene units are metres
MARGIN = 3.0
PLATE_T = 4.0      # faceplate thickness
KEY_T = 3.2        # key protrusion above the plate
PRESS = 0.8        # key travel


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    return scene


def material(name, base, rough, metal=0.0, coat=0.0, bump=0.0, spec=0.5):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*base, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    bsdf.inputs["Coat Weight"].default_value = coat
    bsdf.inputs["Specular IOR Level"].default_value = spec
    if bump:
        # Fine powder-coat / textured plastic grain.
        noise = nodes.new("ShaderNodeTexNoise")
        noise.inputs["Scale"].default_value = 2600.0
        noise.inputs["Detail"].default_value = 6.0
        bmp = nodes.new("ShaderNodeBump")
        bmp.inputs["Strength"].default_value = bump
        bmp.inputs["Distance"].default_value = 0.00005
        links.new(noise.outputs["Fac"], bmp.inputs["Height"])
        links.new(bmp.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def rounded_box(name, x, y, z, w, h, t, radius, mat, segments=6):
    bpy.ops.mesh.primitive_cube_add(size=1, location=(x * MM, y * MM, z * MM))
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = (w * MM, h * MM, t * MM)
    bpy.ops.object.transform_apply(scale=True)
    bev = obj.modifiers.new("bevel", "BEVEL")
    bev.width = min(radius, t / 2 - 0.01, w / 2 - 0.01, h / 2 - 0.01) * MM
    bev.segments = segments
    bev.limit_method = "NONE"
    obj.data.materials.append(mat)
    for poly in obj.data.polygons:
        poly.use_smooth = True
    obj.modifiers.new("wn", "WEIGHTED_NORMAL")
    return obj


def cylinder(name, x, y, z, d, t, mat, bevel=0.6, verts=64):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=d / 2 * MM, depth=t * MM, location=(x * MM, y * MM, z * MM))
    obj = bpy.context.active_object
    obj.name = name
    bev = obj.modifiers.new("bevel", "BEVEL")
    bev.width = bevel * MM
    bev.segments = 6
    bev.limit_method = "ANGLE"
    obj.data.materials.append(mat)
    for poly in obj.data.polygons:
        poly.use_smooth = True
    obj.modifiers.new("wn", "WEIGHTED_NORMAL")
    return obj


def cut(target, cutter):
    mod = target.modifiers.new("cut", "BOOLEAN")
    mod.operation = "DIFFERENCE"
    mod.solver = "EXACT"
    mod.object = cutter
    cutter.hide_render = True
    cutter.hide_viewport = True
    # Keep the bevel after the cut so the opening edges are rounded too.
    target.modifiers.move(len(target.modifiers) - 1, 0)


scene = reset()
paint = material("faceplate", (0.030, 0.031, 0.034), 0.62, bump=0.35)
bezel_paint = material("bezel", (0.022, 0.023, 0.026), 0.48, bump=0.2)
key_plastic = material("key", (0.018, 0.018, 0.020), 0.38, coat=0.15, bump=0.08)
screw_metal = material("screw", (0.13, 0.13, 0.14), 0.34, metal=1.0)
glass = material("glass", (0.004, 0.005, 0.006), 0.04, spec=0.9, coat=0.6)
lens = material("lens", (0.02, 0.02, 0.025), 0.1, spec=0.8)

# Faceplate with the screen opening cut through.
plate = rounded_box("faceplate", 0, 0, -PLATE_T / 2, L.PANEL_W, L.PANEL_H, PLATE_T, 3.0, paint, segments=8)
opening = rounded_box("screen_opening", 0, L.SCREEN_CY, -PLATE_T / 2, L.SCREEN_BEZEL_W - 1.0, L.SCREEN_BEZEL_H - 1.0, PLATE_T * 3, 2.0, paint)
cut(plate, opening)

# Raised screen bezel ring with the glass set back inside it.
bezel = rounded_box("screen_bezel", 0, L.SCREEN_CY, 0.6, L.SCREEN_BEZEL_W, L.SCREEN_BEZEL_H, 1.6, 1.4, bezel_paint)
window = rounded_box("screen_window", 0, L.SCREEN_CY, 0.6, L.SCREEN_GLASS_W, L.SCREEN_GLASS_H, 6.0, 1.2, bezel_paint)
cut(bezel, window)
rounded_box("screen_glass", 0, L.SCREEN_CY, -1.6, L.SCREEN_GLASS_W + 1.0, L.SCREEN_GLASS_H + 1.0, 0.8, 0.3, glass)

# Fasteners: domed slotted heads on a flush collar.
for i, (sx, sy) in enumerate(L.screws()):
    cylinder(f"screw_collar_{i}", sx, sy, 0.15, 7.6, 0.6, bezel_paint, bevel=0.25)
    head = cylinder(f"screw_{i}", sx, sy, 0.9, 6.4, 1.6, screw_metal, bevel=0.7)
    slot = rounded_box(f"slot_{i}", sx, sy, 1.9, 7.0, 1.0, 1.6, 0.1, screw_metal)
    slot.rotation_euler[2] = math.radians(35 + 20 * i)
    cut(head, slot)

for i, sx in enumerate((-L.LDR_X, L.LDR_X)):
    cylinder(f"ldr_ring_{i}", sx, L.LDR_Y, 0.15, L.LDR_D + 1.2, 0.5, bezel_paint, bevel=0.2)
    cylinder(f"ldr_{i}", sx, L.LDR_Y, 0.2, L.LDR_D, 0.5, lens, bevel=0.2)

annunciators = L.annunciators()
for a in annunciators:
    rounded_box(f"ann_{a['id']}", a["x"], a["y"], 0.35, a["w"], a["h"], 0.9, 0.6, glass)

keys = L.keys()
key_objects = []
for k in keys:
    z = KEY_T / 2
    if k["kind"] == "round":
        obj = cylinder(f"key_{k['id']}", k["x"], k["y"], z, k["w"], KEY_T, key_plastic, bevel=1.1)
    else:
        radius = 1.0 if k["kind"] == "lsk" else 1.3
        obj = rounded_box(f"key_{k['id']}", k["x"], k["y"], z, k["w"], k["h"], KEY_T, radius, key_plastic)
    key_objects.append(obj)

# Lighting: large soft key light from above the panel, a cool fill from the left, and a dim studio world.
world = bpy.data.worlds.new("studio")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.18, 0.19, 0.21, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.2


def area_light(name, loc, rot, size, energy, color=(1, 1, 1)):
    data = bpy.data.lights.new(name, "AREA")
    data.shape = "RECTANGLE"
    data.size, data.size_y = size[0], size[1]
    data.energy = energy
    data.color = color
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    obj.location = loc
    obj.rotation_euler = rot
    return obj


area_light("key_light", (0.0, 0.32, 0.30), (math.radians(-47), 0, 0), (0.45, 0.12), 3.6)
area_light("fill_left", (-0.30, 0.05, 0.22), (math.radians(-10), math.radians(-52), 0), (0.2, 0.3), 1.4, (0.9, 0.95, 1.0))
area_light("rim_bottom", (0.0, -0.35, 0.12), (math.radians(70), 0, 0), (0.4, 0.05), 0.9)

# Orthographic front camera: a flat panel that still reads as 3D through bevels, shadows and speculars.
cam_data = bpy.data.cameras.new("front")
cam_data.type = "ORTHO"
frame_w, frame_h = L.PANEL_W + 2 * MARGIN, L.PANEL_H + 2 * MARGIN
cam_data.ortho_scale = max(frame_w, frame_h) * MM
cam = bpy.data.objects.new("front", cam_data)
scene.collection.objects.link(cam)
cam.location = (0, 0, 0.5)
scene.camera = cam

scene.render.engine = "CYCLES"
scene.cycles.samples = SAMPLES
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = round(frame_w * PPMM)
scene.render.resolution_y = round(frame_h * PPMM)
scene.render.resolution_percentage = 100
scene.view_settings.view_transform = "AgX"
scene.view_settings.look = "AgX - Base Contrast"
scene.render.image_settings.file_format = "WEBP"
scene.render.image_settings.color_mode = "RGBA"
scene.render.image_settings.quality = 88

try:
    prefs = bpy.context.preferences.addons["cycles"].preferences
    prefs.compute_device_type = "HIP"
    prefs.refresh_devices()
    for device in prefs.devices:
        device.use = device.type == "HIP"
    scene.cycles.device = "GPU" if any(d.use for d in prefs.devices) else "CPU"
except Exception:
    scene.cycles.device = "CPU"

W, H = scene.render.resolution_x, scene.render.resolution_y


def to_px(x, y):
    # Ortho camera centred on the plate; the wider frame axis spans ortho_scale.
    scale = W / (cam_data.ortho_scale / MM) if W >= H else H / (cam_data.ortho_scale / MM)
    return (W / 2 + x * scale, H / 2 - y * scale, scale)


def rect(item):
    cx, cy, s = to_px(item["x"], item["y"])
    return dict(x=round(cx - item["w"] * s / 2, 1), y=round(cy - item["h"] * s / 2, 1),
                w=round(item["w"] * s, 1), h=round(item["h"] * s, 1))


_, _, scale = to_px(0, 0)
cx, cy, _ = to_px(0, L.SCREEN_CY)
layout = dict(
    source="CMA-9000 faceplate: CMC datasheet CMC-CMA9000-FMS-RMS-19-003 and Operator's Manual Fig. 2-1 to 2-9",
    image=dict(w=W, h=H, pxPerMm=round(scale, 4)),
    screen=dict(x=round(cx - L.SCREEN_ACTIVE_W * scale / 2, 1), y=round(cy - L.SCREEN_ACTIVE_H * scale / 2, 1),
                w=round(L.SCREEN_ACTIVE_W * scale, 1), h=round(L.SCREEN_ACTIVE_H * scale, 1), lines=14, columns=24),
    keys=[dict(id=k["id"], kind=k["kind"], role=k["role"], **rect(k)) for k in keys],
    annunciators=[dict(id=a["id"], **rect(a)) for a in annunciators],
)
with open(os.path.join(OUT, "layout.json"), "w") as f:
    json.dump(layout, f, indent=1)

scene.render.filepath = os.path.join(OUT, "panel.webp")
bpy.ops.render.render(write_still=True)
for obj in key_objects:
    obj.location.z -= PRESS * MM
scene.render.filepath = os.path.join(OUT, "pressed.webp")
bpy.ops.render.render(write_still=True)
for obj in key_objects:
    obj.location.z += PRESS * MM
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "cdu.blend"))
print("CDU-BUILD done", W, H, OUT)
