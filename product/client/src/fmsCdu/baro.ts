// The barometric altitude system (plan rev 3 B1.1): a sensor derived from the physical height, never written back into
// it. The truth is the physical height above MSL. The barometric altitude is that height plus an injectable error: what
// a correctly set altimeter would read, referenced to the declared QNH of the scenario's atmosphere. What the crew sees
// (the indicated altitude) adds the difference between the declared QNH and the crew's setting (QNH or STD), by the ISA
// pressure–altitude relation.
//
// Neither the setting nor the error ever moves the aircraft directly, and neither changes the radio height, which is
// measured from the physical height (surface.ts). An error does affect the truth indirectly, as it would in an
// aircraft: the autopilot holds the barometric altitude, so the aircraft flies where the erroneous altimeter says the
// held altitude is (rev 3.1 addendum, B1.1 wording). The setting changes what is indicated only: the autopilot and the
// FMS work on the barometric altitude referenced to the declared QNH (a laboratory simplification: in an aircraft, a
// wrong setting also moves where the autopilot levels at a preselected altitude).

/** The ISA sea-level pressure, hPa: the STD setting. */
export const STANDARD_HPA = 1013.25;
/** The settings the bench accepts, hPa (the altimeter's range, about 27.5 to 32.5 inHg). */
export const SETTING_RANGE_HPA = { min: 930, max: 1100 } as const;
/** The largest barometric error the bench injects, feet either way. */
export const MAX_BARO_ERROR_FT = 2000;

/**
 * The ISA pressure altitude of a static pressure, feet: 145,366.45 × (1 − (p / 1013.25)^0.190284) (the ISA
 * troposphere, ICAO Doc 7488; as the US National Weather Service states it).
 */
export function pressureAltitudeFt(hPa: number): number {
  return 145366.45 * (1 - Math.pow(hPa / STANDARD_HPA, 0.190284));
}

/** The crew's altimeter setting: STD (1013.25 hPa), or a QNH in hPa. */
export type BaroSetting = { kind: "STD" } | { kind: "QNH"; hPa: number };

export const settingHpa = (setting: BaroSetting) => (setting.kind === "STD" ? STANDARD_HPA : setting.hPa);

/**
 * What the altimeter indicates with a setting, from the barometric altitude referenced to the declared QNH: an altimeter
 * shows the ISA height of its static pressure above the pressure level it is set to, so the reading is the barometric
 * altitude plus the ISA height of the declared QNH above the set one. Set to the declared QNH, the two agree; set to
 * STD, it is the pressure altitude; set higher than the QNH, it reads high.
 */
export function indicatedAltitudeFt(baroAltitudeFt: number, declaredQnhHpa: number, setting: BaroSetting): number {
  return baroAltitudeFt + pressureAltitudeFt(declaredQnhHpa) - pressureAltitudeFt(settingHpa(setting));
}

/** Why a setting is refused, or null: outside the altimeter's range, or not a number. */
export function settingProblem(setting: BaroSetting): string | null {
  if (setting.kind === "STD") return null;
  if (!Number.isFinite(setting.hPa)) return "a QNH needs a number of hPa";
  return setting.hPa < SETTING_RANGE_HPA.min || setting.hPa > SETTING_RANGE_HPA.max ? `a QNH is ${SETTING_RANGE_HPA.min} to ${SETTING_RANGE_HPA.max} hPa` : null;
}

/** Why an injected error is refused, or null. */
export function errorProblem(errorFt: number): string | null {
  if (!Number.isFinite(errorFt)) return "a baro error needs a number of feet";
  return Math.abs(errorFt) > MAX_BARO_ERROR_FT ? `a baro error is at most ${MAX_BARO_ERROR_FT} ft either way` : null;
}

/** The setting as the PFD and the logs write it: STD, or the QNH in whole hPa. */
export const formatSetting = (setting: BaroSetting) => (setting.kind === "STD" ? "STD" : `QNH ${Math.round(setting.hPa)}`);
