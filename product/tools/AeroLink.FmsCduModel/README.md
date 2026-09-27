# CMA-9000 CDU model

Builds the photorealistic CMA-9000 FMS control display unit used by the client's **FMS Test Bench**
(`product/client/src/fmsCdu`). Blender renders the faceplate; the client draws the key legends, annunciator legends,
lamps and the live 14 x 24 display on top, so the nine hardware variations are data rather than nine sets of renders.

| File | Role |
|---|---|
| `cdu_layout.py` | Faceplate geometry in millimetres: outline, fastener spacing, display, every key and annunciator. |
| `build_cdu.py` | Builds the scene in Blender, renders it and writes the client assets. |

Sources: the CMC datasheet *CMC-CMA9000-FMS-RMS-19-003* (approved for public release) for the outline, fastener
spacing, display size and key counts, and the CMA-9000 Operator's Manual, Figures 2-1 to 2-9, for element positions.
No manufacturer logo or name is modelled.

## Output

Written to the directory given on the command line; copy the first three into `product/client/public/fms-cdu/`.

- `panel.webp`: the faceplate with every key up, transparent background.
- `pressed.webp`: the same camera with every key pressed 0.8 mm into the panel. The client shows the pressed
  key's region of this image while it is held.
- `layout.json`: every key, annunciator and the display's active area as rectangles in image pixels.
- `cdu.blend`: the scene, for inspection in Blender. Not committed.

## Regenerate

Run a separate, headless Blender with its own configuration folders, so it cannot touch another Blender
session's preferences, add-ons, recent files or autosaves:

```powershell
$work = Join-Path $env:TEMP 'aerolink-blender'
$env:BLENDER_USER_CONFIG = "$work\config"; $env:BLENDER_USER_SCRIPTS = "$work\scripts"; $env:TEMP = "$work\temp"
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --factory-startup `
  --python product\tools\AeroLink.FmsCduModel\build_cdu.py -- "$work\out" --samples 160 --ppmm 8
```

`--ppmm` is pixels per millimetre (8 gives 1216 x 1420); `--samples` is the Cycles sample count. Cycles uses a HIP
GPU when one is available and the CPU otherwise. Do not pipe Blender's output into a command that exits early,
such as `head`: a closed pipe ends Blender before the second render is written.

Changing geometry moves key rectangles, so commit `layout.json` together with the two images.
