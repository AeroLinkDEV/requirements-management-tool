import { useCallback, useEffect, useLayoutEffect, useRef, useSyncExternalStore } from "react";
import { VELOCITY_VECTOR_MAX_KT, VELOCITY_VECTOR_PX_PER_KT, type AircraftData, type FmsOutputs, type RoutePoint } from "./efis";
import { toLocal, type LatLon } from "./fmsModel";
import { ACTIVE_PROFILE } from "./profile";
import { SyntheticVisionLayer } from "./FmsSyntheticVision";
import { SVS_ZOOM } from "./syntheticVision";
import type { TerrainTiles } from "./terrainTiles";
import "./FmsEfis.css";

/** The coordinated-flight speed (knots): below it a bank does not give the coordinated turn rate. */
const FMA_BOX_MS = ACTIVE_PROFILE.parameters.fmaCaptureBox.value * 1000;
const COORDINATED_BELOW = ACTIVE_PROFILE.parameters.coordinatedLeaveBelow.value;

// A generic EFIS for the bench: a primary flight display and a navigation display, drawn only from the FMS output bus
// and the aircraft data (efis.ts). Colour conventions follow common airline and FAA practice (FAA-H-8083-6; Boeing
// 737 FCOM 10 and 11): magenta for what the FMS commands (targets, active route and waypoint, deviations), green for
// engaged modes, white for armed modes and inactive route data, cyan for crew-selected values, amber for flags. A real
// CMA-9000 installation drives whatever EFIS the aircraft has; these are not that EFIS's exact pages.

const MAGENTA = "#ff5ad9", GREEN = "#43e37c", CYAN = "#48d4ff", WHITE = "#f2f4f7", AMBER = "#ffb020";

// SVG presentation sizes are scene units, so the responsive layout must compensate when it shrinks
// the instruments. Keep larger authored type and enforce the same 12 screen-pixel floor as the workspace.
const readableFont = (authored: number) => `max(${authored}px, var(--efis-readable-font-floor, 12px))`;
function useReadableInstrument() {
  const ref = useRef<SVGSVGElement>(null);
  useLayoutEffect(() => {
    const svg = ref.current;
    if (!svg) return;
    const update = () => {
      const matrix = svg.getScreenCTM();
      if (!matrix) return;
      const determinant = matrix.a * matrix.d - matrix.b * matrix.c;
      // Avoid discriminant cancellation for a uniform scale or rotation.
      const maximum = (Math.hypot(matrix.a + matrix.d, matrix.b - matrix.c) +
        Math.hypot(matrix.a - matrix.d, matrix.b + matrix.c)) / 2;
      const scale = maximum ? Math.abs(determinant) / maximum : 0;
      // A hidden station has no readable size. ResizeObserver measures it again when it becomes visible.
      // A small upward rounding margin survives CSS font-size serialization without lowering the floor.
      if (scale > 0) svg.style.setProperty("--efis-readable-font-floor", `${12.01 / scale}px`);
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(svg);
    return () => observer.disconnect();
  }, []);
  return ref;
}

const three = (deg: number) => String(Math.round(((deg % 360) + 360) % 360) || 360).padStart(3, "0");
// On FMS failure, independent aircraft heading remains available as explicitly TRUE. Wind is always TRUE.
const reference = (bus: FmsOutputs) => bus.failed ? "TRUE" : bus.angleReference;
const variation = (bus: FmsOutputs) => reference(bus) === "MAG" && bus.magneticVariation.status === "NORMAL" ? bus.magneticVariation.value! : 0;
const angularAvailable = (bus: FmsOutputs) => reference(bus) === "TRUE" || bus.magneticVariation.status === "NORMAL";
const angular = (bus: FmsOutputs, angle: number) => `${angularAvailable(bus) ? three(angle - variation(bus)) : "---"}${reference(bus) === "TRUE" ? "T" : "°"}`;
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
const utc = (ms: number) => { const d = new Date(ms); return `${String(d.getUTCHours()).padStart(2, "0")}${String(d.getUTCMinutes()).padStart(2, "0")}.${Math.floor(d.getUTCSeconds() / 6)}Z`; };

/** Installation-specific bench RMI: heading-up card and two independent raw ADF pointers (M300 13-23/24).
 * Bearings are heading-relative bus words, independent of the track-up map and of MAG/TRUE card selection. */
function Rmi({ bus, air }: { bus: FmsOutputs; air: AircraftData }) {
  const cx = 88, cy = 279, radius = 43;
  const heading = air.heading - variation(bus);
  // The routed bench scales the ND; keep the RMI readable at its supported density widths.
  return <g data-testid="nd-rmi" fontSize={readableFont(18)} textAnchor="middle">
    <rect x="10" y="204" width="156" height="191" rx="6" fill="#05070a" stroke="#6a7384" />
    <text x={cx} y="225" fill={WHITE}>RMI (BENCH)</text>
    <circle cx={cx} cy={cy} r={radius} fill="none" stroke={WHITE} />
    {angularAvailable(bus) ? <g fill={WHITE} stroke={WHITE}>
      {Array.from({ length: 12 }, (_, i) => i * 30).map(degrees => {
        const radians = (degrees - heading) * Math.PI / 180;
        return <g key={degrees}>
          <line x1={cx + radius * Math.sin(radians)} y1={cy - radius * Math.cos(radians)} x2={cx + (radius - 5) * Math.sin(radians)} y2={cy - (radius - 5) * Math.cos(radians)} />
          {degrees % 90 === 0 ? <text x={cx + (radius - 17) * Math.sin(radians)} y={cy - (radius - 17) * Math.cos(radians) + 5} stroke="none">{["N", "E", "S", "W"][degrees / 90]}</text> : null}
        </g>;
      })}
    </g> : null}
    <polygon points={`${cx},${cy - radius + 3} ${cx - 4},${cy - radius - 4} ${cx + 4},${cy - radius - 4}`} fill={WHITE} />
    {(["adf", "adf2"] as const).map((device, i) => {
      const word = bus.radioMeasurements[device].adfBearing;
      const valid = word.status === "NORMAL" && word.value !== null && Number.isFinite(word.value);
      const colour = i === 0 ? CYAN : GREEN;
      return <g key={device}>
        {valid ? <g data-testid={`rmi-${device}-needle`} transform={`rotate(${word.value} ${cx} ${cy})`} stroke={colour} strokeWidth="1.8" fill={colour}>
          {i === 0 ? <line x1={cx} y1={cy + 33} x2={cx} y2={cy - 30} /> : <>
            <line x1={cx - 2} y1={cy + 33} x2={cx - 2} y2={cy - 30} />
            <line x1={cx + 2} y1={cy + 33} x2={cx + 2} y2={cy - 30} />
          </>}
          <polygon points={`${cx},${cy - 37} ${cx - 4},${cy - 29} ${cx + 4},${cy - 29}`} stroke="none" />
        </g> : null}
        <text x={cx} y={365 + i * 23} fill={valid ? colour : AMBER} data-testid={`rmi-${device}-value`}>{`ADF${i + 1} ${valid ? `${three(word.value!)} REL` : word.status === "FAIL" ? "FAIL" : "NCD"}`}</text>
      </g>;
    })}
    <circle cx={cx} cy={cy} r="3" fill={WHITE} />
    <text x={cx} y="342" fill={angularAvailable(bus) ? WHITE : AMBER} data-testid="rmi-heading">{angular(bus, air.heading)}</text>
  </g>;
}

/**
 * Newly engaged modes are boxed for ten seconds, as airline mode annunciators do, so a change the crew did not command
 * is noticed. The box times use the simulation clock.
 */
function useModeChangeBoxes(modes: Record<string, string>, now: number) {
  const seen = useRef<Record<string, { mode: string; since: number }>>({});
  const boxed: Record<string, boolean> = {};
  for (const [slot, mode] of Object.entries(modes)) {
    const previous = seen.current[slot];
    if (!previous || previous.mode !== mode) seen.current[slot] = { mode, since: previous ? now : now - FMA_BOX_MS };
    boxed[slot] = now - seen.current[slot].since < FMA_BOX_MS;
  }
  return boxed;
}

/** The primary flight display. The bench places it and the navigation display beside the CDU. */
export function Pfd({ bus, air, now, svs = null }: { bus: FmsOutputs; air: AircraftData; now: number; svs?: TerrainTiles | null }) {
  const instrument = useReadableInstrument();
  // Synthetic vision replaces the sky and ground only while terrain is arriving; chosen but without terrain, the PFD
  // keeps its conventional attitude and flags SVS in amber, as a real SVS is removed and flagged when it loses its data.
  const subscribe = useCallback((listener: () => void) => (svs ? svs.subscribe(listener) : () => undefined), [svs]);
  const terrain = useSyncExternalStore(subscribe, () => svs?.status ?? null);
  const synthetic = terrain === "live";
  const svsFlag = terrain === "off" || terrain === "unreachable";
  // Chosen before any terrain has come, ask for the ground under the aircraft: its answer decides picture or flag.
  const { lat, lon } = air.position;
  useEffect(() => { if (svs && terrain === "waiting") svs.heightAt(lat, lon, SVS_ZOOM); }, [svs, terrain, lat, lon]);
  const heli = air.helicopter;
  const boxed = useModeChangeBoxes({ lateral: bus.lateralMode, vertical: bus.verticalMode ?? "", collective: heli?.axes.collective ?? "", pitch: heli?.axes.pitch ?? "", roll: heli?.axes.roll ?? "" }, now);
  // The helicopter profile shows the indicated airspeed, and none where it is unreliable (dashes, the tape at zero).
  const shownSpeed = heli ? air.ias ?? 0 : air.airspeed;
  const signed = (kt: number) => `${kt < 0 ? "-" : "+"}${Math.abs(kt).toFixed(1)}`;
  const pitchPx = 6; // pixels per degree of pitch
  const cx = 210, cy = 196;
  const headingReference = air.heading - variation(bus);
  // Speed and altitude tapes.
  const speedScale = 3; // px per knot
  const altScale = 0.3; // px per foot
  const speedTicks = Array.from({ length: 17 }, (_, i) => Math.round(shownSpeed / 10) * 10 + (i - 8) * 10).filter(v => v >= 0);
  const altTicks = Array.from({ length: 13 }, (_, i) => Math.round(air.altitude / 100) * 100 + (i - 6) * 100);
  const lateralDots = bus.crossTrack.status === "NORMAL" ? clamp(bus.crossTrack.value! / bus.lateralFullScaleNm, -1.1, 1.1) : null;
  const verticalDots = bus.verticalDeviation.status === "NORMAL" ? clamp(bus.verticalDeviation.value! / bus.verticalFullScaleFt, -1.1, 1.1) : null;
  // Flight director: roll from the FMS roll command (label 121) when LNAV guides; pitch toward the path or the target.
  const fdRoll = bus.rollCommand.status === "NORMAL" ? clamp(bus.rollCommand.value! - air.bank, -20, 20) : null;
  const fdPitch = bus.failed ? null
    : verticalDots !== null && bus.verticalCoupled ? clamp(-verticalDots * 4, -6, 6)
    : bus.targetAltitude.status === "NORMAL" ? clamp((bus.targetAltitude.value! - air.altitude) / 150 - air.pitch, -6, 6) : null;
  const approachLabel = bus.approach.type && bus.approach.state !== "OFF" ? bus.approach.type : null;
  return (
    <svg ref={instrument} fontSize="var(--efis-readable-font-floor, 12px)" className="efisPfd" viewBox="0 0 420 400" role="img" aria-label={`Primary flight display: ${bus.lateralMode} ${bus.verticalMode ?? ""}${bus.failed ? ", FMS failed" : ""}`}>
      <rect width="420" height="400" fill="#05070a" />
      {/* Flight mode annunciator. The helicopter profile: the autopilot's axes, collective, pitch and roll/yaw (AW189
          layout, AAIB-27585); captured green, boxed when new; below each, the modes armed on it in white and a mode a
          failure just took away in amber (B4.1, B3.4). Otherwise: speed, lateral and vertical columns. */}
      {heli ? (
        <g className="efisFma" fontSize={readableFont(15)} fontFamily="inherit" textAnchor="middle" data-testid="fma-axes">
          <line x1="140" y1="4" x2="140" y2="44" stroke="#3a4250" />
          <line x1="280" y1="4" x2="280" y2="44" stroke="#3a4250" />
          <text x="70" y="22" fill={GREEN} data-testid="fma-collective">{heli.axes.collective}</text>
          {boxed.collective ? <rect x="20" y="7" width="100" height="20" fill="none" stroke={GREEN} /> : null}
          <text x="210" y="22" fill={GREEN} data-testid="fma-pitch">{heli.axes.pitch}</text>
          {boxed.pitch ? <rect x="160" y="7" width="100" height="20" fill="none" stroke={GREEN} /> : null}
          <text x="350" y="22" fill={GREEN} data-testid="fma-roll">{heli.axes.roll}</text>
          {boxed.roll ? <rect x="298" y="7" width="104" height="20" fill="none" stroke={GREEN} /> : null}
          {(["collective", "pitch", "roll"] as const).map((axis, column) => {
            // The FMS's own armed approach level (LPV, LNAV/VNAV) belongs with the collective, which flies the vertical.
            const armed = axis === "collective" ? [...heli.armed.collective, ...bus.verticalArmed] : heli.armed[axis];
            return (
              <text key={axis} x={70 + column * 140} y="40" fontSize={readableFont(12)} data-testid={`fma-${axis}-second`}>
                <tspan fill={WHITE} data-testid={`fma-${axis}-armed`}>{armed.join(" ")}</tspan>
                {heli.degraded[axis].length ? <tspan fill={AMBER} dx={armed.length ? 6 : 0} data-testid={`fma-${axis}-degraded`}>{heli.degraded[axis].join(" ")}</tspan> : null}
              </text>
            );
          })}
        </g>
      ) : (
      <g className="efisFma" fontSize={readableFont(15)} fontFamily="inherit" textAnchor="middle">
        <line x1="140" y1="4" x2="140" y2="44" stroke="#3a4250" />
        <line x1="280" y1="4" x2="280" y2="44" stroke="#3a4250" />
        <text x="70" y="22" fill={bus.targetSpeed.status === "NORMAL" ? GREEN : AMBER}>{bus.targetSpeed.status === "NORMAL" ? "FMS SPD" : "SPD"}</text>
        <text x="210" y="22" fill={GREEN} data-testid="fma-lateral">{bus.lateralMode}</text>
        {boxed.lateral ? <rect x="160" y="7" width="100" height="20" fill="none" stroke={GREEN} /> : null}
        <text x="210" y="40" fill={WHITE} fontSize={readableFont(12)}>{bus.lateralArmed.join(" ")}</text>
        <text x="350" y="22" fill={GREEN} data-testid="fma-vertical">{bus.verticalMode ?? ""}</text>
        {boxed.vertical ? <rect x="298" y="7" width="104" height="20" fill="none" stroke={GREEN} /> : null}
        <text x="350" y="40" fill={WHITE} fontSize={readableFont(12)}>{bus.verticalArmed.join(" ")}</text>
      </g>
      )}
      {/* Attitude: sky and ground move with pitch and bank; the aircraft symbol is fixed. */}
      <defs>
        <clipPath id="efisAtt"><rect x="100" y="60" width="220" height="240" rx="18" /></clipPath>
      </defs>
      <g clipPath="url(#efisAtt)">
        {synthetic ? (
          <g transform={`rotate(${-air.bank} ${cx} ${cy})`}>
            <SyntheticVisionLayer air={air} tiles={svs!} cx={cx} cy={cy} pitchPx={pitchPx} />
          </g>
        ) : null}
        <g transform={`rotate(${-air.bank} ${cx} ${cy}) translate(0 ${air.pitch * pitchPx})`}>
          {synthetic ? null : (
            <>
              <rect x="-200" y={cy - 600} width="820" height="600" fill="#1f6fbf" />
              <rect x="-200" y={cy} width="820" height="600" fill="#7a4a1f" />
            </>
          )}
          <line x1="-200" y1={cy} x2="620" y2={cy} stroke={WHITE} strokeWidth="2" />
          {[-20, -10, -5, 5, 10, 20].map(p => (
            <g key={p}>
              <line x1={cx - (Math.abs(p) % 10 === 0 ? 30 : 15)} y1={cy - p * pitchPx} x2={cx + (Math.abs(p) % 10 === 0 ? 30 : 15)} y2={cy - p * pitchPx} stroke={WHITE} strokeWidth="1.5" />
              {Math.abs(p) % 10 === 0 ? <text x={cx + 38} y={cy - p * pitchPx + 4} fill={WHITE} fontSize={readableFont(12)}>{Math.abs(p)}</text> : null}
            </g>
          ))}
        </g>
        {/* Flight director bars (magenta): steering the FMS commands. Removed when there is nothing valid to steer. */}
        {fdRoll !== null ? <line x1={cx + fdRoll * 3} y1={cy - 60} x2={cx + fdRoll * 3} y2={cy + 60} stroke={MAGENTA} strokeWidth="3" data-testid="fd-roll" /> : null}
        {fdPitch !== null ? <line x1={cx - 60} y1={cy - fdPitch * pitchPx} x2={cx + 60} y2={cy - fdPitch * pitchPx} stroke={MAGENTA} strokeWidth="3" /> : null}
      </g>
      {/* Bank scale and pointer. */}
      <g stroke={WHITE} strokeWidth="2">
        {[-60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60].map(a => {
          const r = 118, rad = ((a - 90) * Math.PI) / 180, len = a % 30 === 0 ? 12 : 7;
          return <line key={a} x1={cx + r * Math.cos(rad)} y1={cy + r * Math.sin(rad)} x2={cx + (r + len) * Math.cos(rad)} y2={cy + (r + len) * Math.sin(rad)} />;
        })}
        <polygon points={`${cx},${cy - 116} ${cx - 7},${cy - 104} ${cx + 7},${cy - 104}`} fill={WHITE} stroke="none" transform={`rotate(${-air.bank} ${cx} ${cy})`} />
      </g>
      {/* Aircraft symbol. */}
      <g stroke="#ffd23a" strokeWidth="4" fill="none">
        <polyline points={`${cx - 60},${cy} ${cx - 20},${cy} ${cx - 12},${cy + 8}`} />
        <polyline points={`${cx + 60},${cy} ${cx + 20},${cy} ${cx + 12},${cy + 8}`} />
        <rect x={cx - 3} y={cy - 3} width="6" height="6" fill="#ffd23a" />
      </g>
      {svsFlag ? <text x="308" y="80" fontSize={readableFont(14)} fill={AMBER} textAnchor="end" data-testid="pfd-svs-flag">SVS</text> : null}
      {heli ? (
        <g fontSize={readableFont(13)} data-testid="pfd-heli">
          {/* Radio height: the readout, or an amber flag when failed; nothing above range or off the declared surface. */}
          {heli.radioHeight.status === "NORMAL" ? <text x={cx} y="290" textAnchor="middle" fill={WHITE} data-testid="pfd-ra">{`RA ${Math.round(heli.radioHeight.value!)}`}</text>
            : heli.radioHeight.status === "FAIL" ? <text x={cx} y="290" textAnchor="middle" fill={AMBER} data-testid="pfd-ra">RA</text> : null}
          {heli.hoverData ? <text x="308" y="290" textAnchor="end" fill={CYAN} data-testid="pfd-hover-height">{`HH ${heli.hoverHeight}`}</text> : null}
          {heli.lowHeight ? <text x={cx} y="80" textAnchor="middle" fill={AMBER} data-testid="pfd-low-height">{heli.lowHeight}</text> : null}
          {/* Hover data: ground velocity in aircraft axes and the wind, where airspeed stops meaning much. */}
          {heli.hoverData ? (
            <g fill={WHITE} data-testid="pfd-hover-data">
              <text x="112" y="256">{heli.vx === null ? "VX ---.-" : `VX ${signed(heli.vx)}`}</text>
              <text x="112" y="272">{heli.vy === null ? "VY ---.-" : `VY ${signed(heli.vy)}`}</text>
              <text x="112" y="290">{`${three(air.wind.direction)}T/${Math.round(air.wind.speed)}`}</text>
              {heli.selectedVelocity ? (
                <g fill={CYAN} data-testid="pfd-selected-velocity">
                  <text x="178" y="256">{signed(heli.selectedVelocity.vx)}</text>
                  <text x="178" y="272">{signed(heli.selectedVelocity.vy)}</text>
                </g>
              ) : null}
            </g>
          ) : null}
        </g>
      ) : null}
      {approachLabel ? <text x="112" y="80" fontSize={readableFont(14)} fill={bus.approach.state === "CAPTURED" ? GREEN : WHITE} data-testid="pfd-approach">{approachLabel}</text> : null}
      {/* Speed tape with the FMS target speed bug (magenta). */}
      <g>
        <clipPath id="efisSpd"><rect x="16" y="60" width="70" height="240" /></clipPath>
        <rect x="16" y="60" width="70" height="240" fill="#2a2f38" />
        <g clipPath="url(#efisSpd)" fontSize={readableFont(12)} fill={WHITE}>
          {speedTicks.map(v => (
            <g key={v}>
              <line x1="72" y1={cy - (v - shownSpeed) * speedScale} x2="86" y2={cy - (v - shownSpeed) * speedScale} stroke={WHITE} />
              <text x="66" y={cy - (v - shownSpeed) * speedScale + 4} textAnchor="end">{v}</text>
            </g>
          ))}
          {bus.targetSpeed.status === "NORMAL" ? (
            <polygon points={`86,${cy - (bus.targetSpeed.value! - air.airspeed) * speedScale} 78,${cy - (bus.targetSpeed.value! - air.airspeed) * speedScale - 7} 78,${cy - (bus.targetSpeed.value! - air.airspeed) * speedScale + 7}`} fill={MAGENTA} />
          ) : null}
        </g>
        <rect x="18" y={cy - 14} width="62" height="28" fill="#000" stroke={WHITE} />
        <text x="72" y={cy + 6} textAnchor="end" fontSize={readableFont(17)} fill={WHITE} data-testid="pfd-speed">{heli && air.ias === null ? "---" : Math.round(shownSpeed)}</text>
        <text x="51" y="54" textAnchor="middle" fontSize={readableFont(13)} fill={bus.targetSpeed.status === "NORMAL" ? MAGENTA : air.selectedSpeed !== null ? CYAN : AMBER}>{bus.targetSpeed.status === "NORMAL" ? Math.round(bus.targetSpeed.value!) : air.selectedSpeed !== null ? air.selectedSpeed : "---"}</text>
      </g>
      {/* Altitude tape: the FMS target altitude (magenta), or the latched altitude hold reference (cyan). */}
      <g>
        <clipPath id="efisAlt"><rect x="336" y="60" width="66" height="240" /></clipPath>
        <rect x="336" y="60" width="66" height="240" fill="#2a2f38" />
        <g clipPath="url(#efisAlt)" fontSize={readableFont(12)} fill={WHITE}>
          {altTicks.map(v => (
            <g key={v}>
              <line x1="336" y1={cy - (v - air.altitude) * altScale} x2="346" y2={cy - (v - air.altitude) * altScale} stroke={WHITE} />
              {v % 200 === 0 ? <text x="350" y={cy - (v - air.altitude) * altScale + 4}>{v}</text> : null}
            </g>
          ))}
          {bus.targetAltitude.status === "NORMAL" ? (
            <rect x="336" y={clamp(cy - (bus.targetAltitude.value! - air.altitude) * altScale - 8, 58, 286)} width="8" height="16" fill={MAGENTA} data-testid="alt-bug" />
          ) : air.selectedAltitude !== null ? (
            <rect x="336" y={clamp(cy - (air.selectedAltitude - air.altitude) * altScale - 8, 58, 286)} width="8" height="16" fill={CYAN} data-testid="alt-bug" />
          ) : null}
        </g>
        <rect x="338" y={cy - 14} width="64" height="28" fill="#000" stroke={WHITE} />
        <text x="398" y={cy + 6} textAnchor="end" fontSize={readableFont(16)} fill={WHITE}>{Math.round(air.altitude)}</text>
        <text x="369" y="54" textAnchor="middle" fontSize={readableFont(13)} fill={bus.targetAltitude.status === "NORMAL" ? MAGENTA : CYAN}>
          {bus.targetAltitude.status === "NORMAL" ? Math.round(bus.targetAltitude.value!) : air.selectedAltitude !== null ? air.selectedAltitude : bus.verticalMode === "ALT HOLD" ? "HOLD" : "----"}
        </text>
        {/* The crew's altimeter setting (cyan, a crew selection): STD, or QNH and hPa (B1.1, baro.ts). */}
        <text x="369" y="316" textAnchor="middle" fontSize={readableFont(12)} fill={CYAN} data-testid="pfd-baro">{air.baroSetting}</text>
      </g>
      {/* The missed approach altitude the selected altitude does not meet (helicopter profile): shown, never flown. */}
      {air.missedAltitudeConflict ? (
        <text x="369" y="318" textAnchor="middle" fontSize={readableFont(12)} fill={AMBER} data-testid="pfd-missed-altitude">{`MA ${air.missedAltitudeConflict}`}</text>
      ) : null}
      {/* Vertical speed. */}
      <g>
        <rect x="404" y="90" width="14" height="180" fill="#2a2f38" />
        <line x1="404" y1={cy} x2="418" y2={clamp(cy - air.verticalSpeed / 20, 92, 268)} stroke={WHITE} strokeWidth="2" />
        <text x="411" y="84" textAnchor="middle" fontSize={readableFont(12)} fill={WHITE}>{Math.round(air.verticalSpeed / 50) * 50}</text>
      </g>
      {/* Vertical deviation (label 117): filled diamond when the path is flown, hollow when only advisory. */}
      {verticalDots !== null ? (
        <g data-testid="vdev">
          {[-1, -0.5, 0.5, 1].map(d => <circle key={d} cx="326" cy={cy + d * 80} r="3" fill="none" stroke={WHITE} />)}
          <line x1="319" y1={cy} x2="333" y2={cy} stroke={WHITE} />
          <polygon points={`326,${cy + verticalDots * 80 - 8} 333,${cy + verticalDots * 80} 326,${cy + verticalDots * 80 + 8} 319,${cy + verticalDots * 80}`}
            fill={bus.verticalCoupled ? MAGENTA : "none"} stroke={MAGENTA} strokeWidth="2" />
          <text x="326" y={cy - 92} textAnchor="middle" fontSize={readableFont(12)} fill={WHITE}>{bus.verticalSource === "APPR" ? "GP" : "VPTH"}</text>
        </g>
      ) : bus.verticalDeviation.status === "FAIL" ? <text x="326" y={cy} textAnchor="middle" fontSize={readableFont(12)} fill={AMBER}>V</text> : null}
      {/* Lateral deviation (label 116), full scale for the phase, and the navigation source annunciation: GPS while the
          lateral steers the selected receiver's 116 on an RNAV final (lateralSource), so a reversion to the route shows. */}
      <g>
        {[-1, -0.5, 0.5, 1].map(d => <circle key={d} cx={cx + d * 90} cy="318" r="3" fill="none" stroke={WHITE} />)}
        <line x1={cx} y1="311" x2={cx} y2="325" stroke={WHITE} />
        {lateralDots !== null ? (
          <polygon data-testid="ldev" points={`${cx + lateralDots * 90 - 8},318 ${cx + lateralDots * 90},311 ${cx + lateralDots * 90 + 8},318 ${cx + lateralDots * 90},325`} fill={MAGENTA} />
        ) : null}
        <text x="104" y="308" fontSize={readableFont(12)} fill={bus.failed ? AMBER : GREEN} data-testid="nav-source">{bus.failed ? "FMS" : `${bus.source} ${bus.phase === "EN ROUTE" ? "ENR" : bus.phase === "TERMINAL" ? "TERM" : "APPR"}${bus.lateralSource === "GPS" ? " GPS" : ""}`}</text>
        <text x="316" y="308" fontSize={readableFont(12)} fill={WHITE} textAnchor="end">{lateralDots !== null ? `${Number(bus.lateralFullScaleNm.toFixed(2))}NM` : ""}</text>
      </g>
      {/* Heading: current heading, the desired track (magenta) and a crew-selected heading (cyan). */}
      <g>
        <clipPath id="efisHdg"><rect x="100" y="334" width="220" height="60" /></clipPath>
        <rect x="100" y="334" width="220" height="60" fill="#2a2f38" />
        <g clipPath="url(#efisHdg)" fontSize={readableFont(12)} fill={WHITE}>
          {Array.from({ length: 25 }, (_, i) => Math.round(headingReference / 5) * 5 + (i - 12) * 5).map(h => {
            const x = cx + (((h - headingReference + 540) % 360) - 180) * 4;
            return (
              <g key={h}>
                <line x1={x} y1="334" x2={x} y2={h % 10 === 0 ? 346 : 340} stroke={WHITE} />
                {h % 30 === 0 && angularAvailable(bus) ? <text x={x} y="360" textAnchor="middle">{three(h)}</text> : null}
              </g>
            );
          })}
          {bus.desiredTrack.status === "NORMAL" ? (
            <line x1={cx + (((bus.desiredTrack.value! - air.heading + 540) % 360) - 180) * 4} y1="334" x2={cx + (((bus.desiredTrack.value! - air.heading + 540) % 360) - 180) * 4} y2="352" stroke={MAGENTA} strokeWidth="3" />
          ) : null}
        </g>
        {/* The heading bug: the selected heading, parked at the edge of the scale when it is off it. */}
        {(() => {
          const x = Math.max(106, Math.min(314, cx + (((air.selectedHeading - air.heading + 540) % 360) - 180) * 4));
          return <polygon data-testid="pfd-selected-heading" points={`${x - 6},334 ${x + 6},334 ${x + 6},342 ${x},337 ${x - 6},342`} fill={CYAN} />;
        })()}
        <polygon points={`${cx},334 ${cx - 6},326 ${cx + 6},326`} fill={WHITE} />
        <rect x={cx - 24} y="366" width="48" height="22" fill="#000" stroke={WHITE} />
        <text x={cx} y="382" textAnchor="middle" fontSize={readableFont(15)} fill={WHITE}>{angular(bus, air.heading)}</text>
        <text x="316" y="382" textAnchor="end" fontSize={readableFont(12)} fill={CYAN} data-testid="pfd-selected-heading-value">{`HDG ${angular(bus, air.selectedHeading)}`}</text>
      </g>
      {bus.failed ? <text x={cx} y="120" textAnchor="middle" fontSize={readableFont(16)} fill={AMBER} data-testid="pfd-fms-flag">FMS FAIL</text> : null}
    </svg>
  );
}

/** The navigation display (MAP mode, track-up). */
export function Nd({ bus, air, range }: { bus: FmsOutputs; air: AircraftData; range: number }) {
  const instrument = useReadableInstrument();
  const cx = 210, cy = 360, radius = 300;
  const trackReference = air.track - variation(bus);
  const px = radius / range;
  // Track-up map: positions relative to the aircraft, rotated so the present track points up.
  const project = (p: LatLon) => {
    const { x, y } = toLocal(air.position, p);
    // Rotate by the track, so a point straight ahead along it lands straight up the screen.
    const a = (air.track * Math.PI) / 180;
    return { x: cx + (x * Math.cos(a) - y * Math.sin(a)) * px, y: cy - (x * Math.sin(a) + y * Math.cos(a)) * px };
  };
  const polyline = (points: LatLon[]) => points.map(p => { const q = project(p); return `${q.x.toFixed(1)},${q.y.toFixed(1)}`; }).join(" ");
  const route = (points: RoutePoint[]) => [air.position, ...points.map(point => point.position)];
  // Position trend vector: where the present bank takes the aircraft in 30, 60 and 90 seconds.
  // Degrees per second from the bank, in coordinated flight only: below its speed a bank does not give this turn rate
  // (the low-speed velocity vector replaces the trend, Stage B4).
  const turnRate = air.airspeed >= COORDINATED_BELOW ? (1091 * Math.tan((air.bank * Math.PI) / 180)) / air.airspeed : 0;
  const trend: { x: number; y: number }[] = [];
  let heading = 0, x = cx, y = cy;
  for (let t = 0; t < 90; t += 5) {
    heading += turnRate * 5;
    const step = (air.groundSpeed / 3600) * 5 * px;
    x += step * Math.sin((heading * Math.PI) / 180);
    y -= step * Math.cos((heading * Math.PI) / 180);
    trend.push({ x, y });
  }
  const headingOffset = ((air.heading - air.track + 540) % 360) - 180;
  // B4.5: in the helicopter's low-speed regime (below the coordinated-flight speed) a turn from bank means nothing, so
  // the trend gives way to the ground velocity: an arrow along the track (up, the map being track-up), its length the
  // ground speed at a fixed scale whatever the range (3 px a knot, to 40 kt). A bench design, labelled as such, since
  // this symbology is installation-specific.
  const lowSpeed = air.helicopter?.lowSpeed === true;
  const velocityLength = Math.min(air.groundSpeed, VELOCITY_VECTOR_MAX_KT) * VELOCITY_VECTOR_PX_PER_KT;
  return (
    <svg ref={instrument} fontSize="var(--efis-readable-font-floor, 12px)" className="efisNd" viewBox="0 0 420 420" role="img" aria-label={`Navigation display, ${range} NM range${bus.failed ? ", map failed" : ""}`}>
      <rect width="420" height="420" fill="#05070a" />
      <defs><clipPath id="efisMap"><circle cx={cx} cy={cy} r={radius} /></clipPath></defs>
      {/* Compass arc: the present track at the top, heading pointer beside it. */}
      <g stroke={WHITE} fill={WHITE} fontSize={readableFont(12)}>
        <path d={`M ${cx - radius * Math.sin(Math.PI / 3)} ${cy - radius * Math.cos(Math.PI / 3)} A ${radius} ${radius} 0 0 1 ${cx + radius * Math.sin(Math.PI / 3)} ${cy - radius * Math.cos(Math.PI / 3)}`} fill="none" />
        {Array.from({ length: 25 }, (_, i) => Math.round(trackReference / 5) * 5 + (i - 12) * 5).map(h => {
          const off = ((h - trackReference + 540) % 360) - 180;
          if (Math.abs(off) > 58) return null;
          const a = (off * Math.PI) / 180;
          const len = h % 10 === 0 ? 12 : 6;
          return (
            <g key={h}>
              <line x1={cx + radius * Math.sin(a)} y1={cy - radius * Math.cos(a)} x2={cx + (radius - len) * Math.sin(a)} y2={cy - (radius - len) * Math.cos(a)} />
              {h % 30 === 0 && angularAvailable(bus) ? <text x={cx + (radius - 24) * Math.sin(a)} y={cy - (radius - 24) * Math.cos(a) + 4} textAnchor="middle" stroke="none">{three(h).slice(0, 2)}</text> : null}
            </g>
          );
        })}
        <polygon points={`${cx + (radius + 2) * Math.sin((headingOffset * Math.PI) / 180)},${cy - (radius + 2) * Math.cos((headingOffset * Math.PI) / 180)} ${cx + (radius + 12) * Math.sin(((headingOffset - 2) * Math.PI) / 180)},${cy - (radius + 12) * Math.cos(((headingOffset - 2) * Math.PI) / 180)} ${cx + (radius + 12) * Math.sin(((headingOffset + 2) * Math.PI) / 180)},${cy - (radius + 12) * Math.cos(((headingOffset + 2) * Math.PI) / 180)}`} fill={WHITE} />
        <rect x={cx - 26} y={cy - radius - 34} width="52" height="20" fill="#000" />
        <text x={cx} y={cy - radius - 19} textAnchor="middle" stroke="none" fontSize={readableFont(14)}>{angular(bus, air.track)} TRK</text>
      </g>
      {/* Half-range arc and track line. */}
      <path d={`M ${cx - radius / 2} ${cy} A ${radius / 2} ${radius / 2} 0 0 1 ${cx + radius / 2} ${cy}`} fill="none" stroke="#6a7384" strokeDasharray="3 6" />
      <text x={cx - radius / 2 - 4} y={cy - 6} fontSize={readableFont(12)} fill={WHITE} textAnchor="end">{range / 2}</text>
      <line x1={cx} y1={cy} x2={cx} y2={cy - radius} stroke="#6a7384" />
      <g clipPath="url(#efisMap)" fontSize={readableFont(12)}>
        {/* Modified route: dashed white; offset: dashed magenta; active route: solid magenta. */}
        {bus.modifiedRoute ? <polyline points={polyline(route(bus.modifiedRoute))} fill="none" stroke={WHITE} strokeWidth="2" strokeDasharray="8 6" data-testid="nd-mod-route" /> : null}
        {bus.offsetTrack ? <polyline points={polyline(bus.offsetTrack)} fill="none" stroke={MAGENTA} strokeWidth="2" strokeDasharray="8 6" /> : null}
        {bus.activeRoute.length ? <polyline points={polyline(route(bus.activeRoute))} fill="none" stroke={MAGENTA} strokeWidth="2.5" data-testid="nd-route" /> : null}
        {bus.activeRoute.map(point => {
          const q = project(point.position);
          const colour = point.active ? MAGENTA : WHITE;
          return (
            <g key={point.ident} data-testid={point.active ? "nd-active-wpt" : undefined}>
              <polygon points={`${q.x},${q.y - 7} ${q.x + 2},${q.y - 2} ${q.x + 7},${q.y} ${q.x + 2},${q.y + 2} ${q.x},${q.y + 7} ${q.x - 2},${q.y + 2} ${q.x - 7},${q.y} ${q.x - 2},${q.y - 2}`} fill="none" stroke={colour} />
              <text x={q.x + 9} y={q.y - 4} fill={colour}>{point.ident}</text>
              {point.constraint ? <text x={q.x + 9} y={q.y + 10} fill={colour} fontSize={readableFont(12)}>{point.constraint}</text> : null}
            </g>
          );
        })}
        {/* Profile points from VNAV: top and end of descent, green circles. */}
        {([["T/D", bus.topOfDescent], ["E/D", bus.endOfDescent]] as const).map(([label, p]) => {
          if (!p) return null;
          const q = project(p);
          return <g key={label} data-testid={`nd-${label === "T/D" ? "tod" : "ed"}`}><circle cx={q.x} cy={q.y} r="5" fill="none" stroke={GREEN} strokeWidth="2" /><text x={q.x + 8} y={q.y + 14} fill={GREEN}>{label}</text></g>;
        })}
        {bus.holdFix ? (() => { const q = project(bus.holdFix); return <ellipse cx={q.x} cy={q.y - 12} rx="9" ry="16" fill="none" stroke={MAGENTA} strokeWidth="2" />; })() : null}
        {/* Position trend vector (white): the path the present bank gives over 90 seconds; at low speed, the ground
            velocity instead (B4.5). */}
        {lowSpeed ? (
          <g data-testid="nd-ground-velocity" stroke={GREEN} fill={GREEN}>
            <line x1={cx} y1={cy} x2={cx} y2={cy - velocityLength} strokeWidth="2.5" />
            {velocityLength > 6 ? <polygon points={`${cx},${cy - velocityLength - 8} ${cx - 5},${cy - velocityLength} ${cx + 5},${cy - velocityLength}`} stroke="none" /> : null}
            <text x={cx + 10} y={cy - velocityLength - 2} stroke="none" fontSize={readableFont(12)}>{Math.round(air.groundSpeed)} KT</text>
            <text x={cx + 12} y={cy + 24} stroke="none" fontSize={readableFont(10)}>GND VEL (BENCH)</text>
          </g>
        ) : (
          <polyline data-testid="nd-trend" points={[{ x: cx, y: cy }, ...trend].map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" ")} fill="none" stroke={WHITE} strokeWidth="1.5" strokeDasharray="10 5" />
        )}
      </g>
      {/* Aircraft symbol. */}
      <polygon points={`${cx},${cy - 12} ${cx - 9},${cy + 10} ${cx},${cy + 5} ${cx + 9},${cy + 10}`} fill="none" stroke={WHITE} strokeWidth="2" />
      {/* Data corners: ground speed, true airspeed and wind; the active waypoint, its distance and ETA. */}
      <g fontSize={readableFont(13)} fill={WHITE}>
        <text x="10" y="20">GS <tspan fontSize={readableFont(16)}>{Math.round(air.groundSpeed)}</tspan>  TAS <tspan fontSize={readableFont(16)}>{Math.round(air.airspeed)}</tspan></text>
        <text x="10" y="38">{three(air.wind.direction)}T/{Math.round(air.wind.speed)}</text>
        {bus.toWaypoint.status === "NORMAL" ? (
          <g textAnchor="end" data-testid="nd-to-wpt">
            <text x="410" y="20" fill={MAGENTA} fontSize={readableFont(15)}>{bus.toWaypoint.value}</text>
            <text x="410" y="38">{bus.distanceToGo.status === "NORMAL" ? `${bus.distanceToGo.value!.toFixed(1)} NM` : ""}</text>
            <text x="410" y="56">{bus.eta.status === "NORMAL" ? utc(bus.eta.value!) : ""}</text>
          </g>
        ) : null}
        <text x="10" y="408" fontSize={readableFont(12)} fill={bus.failed ? AMBER : GREEN} data-testid="nd-source">{bus.failed ? "MAP" : `${bus.source} ${bus.navMode}`}</text>
        {!bus.failed ? <text x="410" y="408" fontSize={readableFont(12)} textAnchor="end">RNP {bus.rnp.toFixed(2)} ANP {bus.anp === null ? "----" : bus.anp.toFixed(2)}</text> : null}
      </g>
      <Rmi bus={bus} air={air} />
      {bus.failed ? <text x={cx} y="200" textAnchor="middle" fontSize={readableFont(18)} fill={AMBER} data-testid="nd-map-flag">MAP</text> : null}
    </svg>
  );
}
