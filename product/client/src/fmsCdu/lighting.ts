/**
 * Cockpit lighting for the panel. The CDU's light sensor (LDR) sets display luminance from the ambient light, and
 * BRT moves it within the range of the lighting mode. NVG mode keeps the display in the 0.1 to 3 fL range that
 * night vision goggles tolerate, and the key and annunciator lighting turns NVIS green.
 */
export type LightingMode = "day" | "night" | "nvg";

export const LIGHTING_MODES: readonly { id: LightingMode; label: string; ambient: number }[] = [
  { id: "day", label: "Day", ambient: 0.8 },
  { id: "night", label: "Night", ambient: 0.1 },
  { id: "nvg", label: "NVG", ambient: 0.02 },
];

/** Display luminance limits in foot-lamberts for each mode. */
export const LUMINANCE_RANGE: Record<LightingMode, { min: number; max: number }> = {
  day: { min: 20, max: 200 },
  night: { min: 1, max: 40 },
  nvg: { min: 0.1, max: 3 },
};

export type Lighting = { mode: LightingMode; ambient: number };

const clamp = (value: number) => Math.min(1, Math.max(0, value));

/**
 * Display luminance in fL from the BRT setting (0..1) and the ambient light seen by the LDR (0..1). Both move the
 * luminance along a logarithmic scale, as the eye perceives brightness, and it never leaves the mode's range.
 */
export function displayLuminance(brt: number, { mode, ambient }: Lighting) {
  const { min, max } = LUMINANCE_RANGE[mode];
  const position = clamp(0.5 * clamp(ambient) + 0.5 * clamp(brt));
  return min * (max / min) ** position;
}

/** The CSS brightness factor that shows a luminance on a desktop screen: 200 fL is full brightness. */
export function screenBrightness(luminance: number) {
  const low = Math.log10(LUMINANCE_RANGE.nvg.min), high = Math.log10(LUMINANCE_RANGE.day.max);
  return 0.2 + 0.8 * clamp((Math.log10(luminance) - low) / (high - low));
}
