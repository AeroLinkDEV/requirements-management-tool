import { expect, logicTest as test } from './isolated-client-test'
import { ALERTS, alert } from '../src/fmsCdu/alerts'
import { holdEntry } from '../src/fmsCdu/fmsModel'
import { LIGHTING_MODES, LUMINANCE_RANGE, displayLuminance } from '../src/fmsCdu/lighting'
import { HELICOPTER_PROFILE, LATER_SBAS_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { COLUMNS, SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import { CDU_VARIANTS, type CduFunction } from '../src/fmsCdu/variants'

// The deeper simulation: direct-to, holds, VNAV, search patterns, the tactical approach, the timer, the injectable
// conditions and alerts, and the lighting model. Page rules follow the CMA-9000 Operator's Manual; key rules are
// proved in fms-cdu-engine.spec.ts.
const fms = (start = Date.UTC(2026, 8, 27, 14, 0, 0), profile = HELICOPTER_PROFILE) => {
  let now = start
  const unit = new ScriptedFms(() => new Date(now), { profile })
  return { unit, advance: (ms: number) => { now += ms } }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === '-' ? 'PLUSMINUS' : `CHAR_${ch}`)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
const titleLine = (unit: ScriptedFms) => lines(unit)[0]
const idents = (unit: ScriptedFms) => unit.route.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : '(disco)'))

test('DIRECT-TO a waypoint in the route deletes the legs before it, as a modification from present position', () => {
  const { unit } = fms()
  unit.press('LEGS')
  enter(unit, 'TOLGU', 'LSK1L')
  expect(titleLine(unit)).toMatch(/^MOD RTE 1 LEGS/)
  expect(idents(unit)).toEqual(['TOLGU', 'DEMEL', 'ALNIT', 'ULIDA', 'FERDI', 'RW24R', 'CYUL'])
  // The leg into TOLGU is computed from present position, not left blank.
  expect(lines(unit)[1]).toMatch(/^ 0\d\d°\s+\d+\.\dNM/)
  unit.press('EXEC')
  expect(titleLine(unit)).toMatch(/^ACT RTE 1 LEGS/)
  expect(unit.lamps().has('EXEC')).toBe(false)
})

test('DIRECT-TO a waypoint off the route goes first with a route discontinuity; an unknown ident is refused', () => {
  const { unit } = fms()
  unit.press('LEGS')
  enter(unit, 'ELIBA', 'LSK1L')
  expect(idents(unit).slice(0, 3)).toEqual(['ELIBA', '(disco)', 'MUN'])
  expect(lines(unit)[3]).toContain('ROUTE DISCONTINUITY')
  expect(lines(unit)[4]).toMatch(/^□{5}/)
  // After a discontinuity there is no computed leg.
  expect(lines(unit)[5]).toMatch(/^ ---°/)
  unit.press('LSK6L')

  enter(unit, 'ZZZZZ', 'LSK1L')
  expect(scratch(unit)).toBe('NOT IN DATA BASE')
  expect(titleLine(unit)).toMatch(/^ACT RTE 1 LEGS/)
  expect(idents(unit)[0]).toBe('MUN')
})

test('a later waypoint entered onto a leg closes the gap by deleting the legs in between', () => {
  const { unit } = fms()
  unit.press('LEGS')
  enter(unit, 'FERDI', 'LSK2L')
  expect(idents(unit)).toEqual(['MUN', 'FERDI', 'RW24R', 'CYUL'])
})

test('hold entry follows the standard sectors: direct 180 degrees, teardrop 70, parallel 110, mirrored for left turns', () => {
  expect(holdEntry(360, 360, 'RIGHT')).toBe('DIRECT')
  expect(holdEntry(110, 360, 'RIGHT')).toBe('DIRECT')
  expect(holdEntry(111, 360, 'RIGHT')).toBe('TEARDROP')
  expect(holdEntry(180, 360, 'RIGHT')).toBe('TEARDROP')
  expect(holdEntry(181, 360, 'RIGHT')).toBe('PARALLEL')
  expect(holdEntry(289, 360, 'RIGHT')).toBe('PARALLEL')
  expect(holdEntry(290, 360, 'RIGHT')).toBe('DIRECT')
  expect(holdEntry(250, 360, 'LEFT')).toBe('DIRECT')
  expect(holdEntry(210, 360, 'LEFT')).toBe('TEARDROP')
  expect(holdEntry(100, 360, 'LEFT')).toBe('PARALLEL')
  expect(holdEntry(90, 270, 'RIGHT')).toBe('TEARDROP')
})

test('a hold is INACTIVE in the modification, ARMED on EXEC, IN PROGRESS at the fix, and exits only when armed to', () => {
  const { unit } = fms()
  press(unit, 'HOLD', 'LSK2L')
  expect(titleLine(unit)).toMatch(/^MOD HOLD/)
  expect(lines(unit)[6]).toMatch(/INACTIVE$/)
  // Arriving on the inbound course is a direct entry; reversing the inbound course makes it a teardrop.
  expect(lines(unit)[8]).toMatch(/DIRECT$/)
  enter(unit, '263', 'LSK3L')
  expect(lines(unit)[8]).toMatch(/TEARDROP$/)
  unit.press('LSK2L')
  expect(lines(unit)[4]).toMatch(/^>LEFT/)
  expect(lines(unit)[8]).toMatch(/PARALLEL$/)
  unit.press('EXEC')
  expect(lines(unit)[6]).toMatch(/ARMED$/)

  unit.sequence()
  expect(idents(unit)[0]).toBe('RDG')
  unit.sequence()
  expect(lines(unit)[6]).toMatch(/IN PROGRESS$/)
  expect(lines(unit)[10]).toMatch(/EXIT HOLD>$/)
  unit.sequence()
  expect(idents(unit)[0]).toBe('RDG')

  unit.press('LSK5R')
  expect(lines(unit)[6]).toMatch(/EXIT ARMED$/)
  unit.press('EXEC')
  unit.sequence()
  expect(idents(unit)[0]).toBe('TOLGU')
  expect(unit.route.hold).toBeUndefined()
})

test('hold fields are range-checked, and entering the hold above the holding speed raises HIGH HOLDING SPEED', () => {
  const { unit } = fms()
  press(unit, 'HOLD', 'LSK1L')
  enter(unit, '400', 'LSK3L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR')
  enter(unit, '/5.0', 'LSK4L')
  expect(lines(unit)[8]).toMatch(/^-\.-MIN\/5\.0NM/)
  enter(unit, '250/6000A', 'LSK1R')
  expect(lines(unit)[2]).toMatch(/250\/ 6000A$/)
  press(unit, 'EXEC')
  unit.sequence()
  expect(scratch(unit)).toBe('HIGH HOLDING SPEED')
  expect(unit.lamps().has('MSG')).toBe(true)
})

test('VNAV derives the path angle from the FAF altitude and alerts outside 2.75 to 3.77 degrees', () => {
  const { unit } = fms(undefined, LATER_SBAS_PROFILE)
  unit.press('VNAV')
  // The runway without RW: the title must fit its approach level beside the page number (GPS phase 3b, R19).
  expect(titleLine(unit)).toMatch(/^ACT VNAV 24R\b/)
  expect(lines(unit)[10]).toMatch(/-2\.9\d°$/)
  expect(lines(unit)[12]).toMatch(/^-----\s+-6\d0FPM$/)

  enter(unit, '2000', 'LSK1R')
  expect(scratch(unit)).toBe('HIGH GLIDEPATH ANGLE')
  expect(lines(unit)[2]).toMatch(/FERDI 2000A$/)
  unit.press('CLR')
  enter(unit, '1100', 'LSK1R')
  expect(scratch(unit)).toBe('LOW GLIDEPATH ANGLE')
  unit.press('CLR')
  enter(unit, '1500', 'LSK1R')
  expect(scratch(unit)).toBe('')
  // FERDI is the seventh leg: the second row of LEGS page 2.
  press(unit, 'LEGS', 'NEXT')
  expect(lines(unit)[4]).toMatch(/^FERDI\s+1500A$/)
})

test('VNAV takes a destination temperature and QNH in range, and shows VDEV once on final', () => {
  const { unit } = fms(undefined, LATER_SBAS_PROFILE)
  unit.press('VNAV')
  enter(unit, '900', 'LSK4R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR')
  enter(unit, '1013', 'LSK4R')
  enter(unit, '-5', 'LSK4L')
  expect(lines(unit)[8]).toMatch(/^-5°C\s+1013$/)
  // Jump through MUN, RDG, TOLGU and the downwind, base and final fixes to the FAF.
  for (let i = 0; i < 7; i += 1) unit.sequence()
  // At the FAF, 3000 ft against a 1500 ft path: 1500 ft high.
  expect(lines(unit)[12]).toMatch(/^\+1500FT/)
})

test('a search pattern is defined on its page, activated as a modification, and flown until INTERRUPT', () => {
  const { unit } = fms()
  press(unit, 'TACT', 'LSK2L')
  expect(titleLine(unit)).toMatch(/^SQUARE SAR\s+1\/3$/)
  unit.press('NEXT')
  expect(titleLine(unit)).toMatch(/^LADDER SAR\s+2\/3$/)
  unit.press('PREV')
  enter(unit, '50', 'LSK1R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR')
  enter(unit, '0.5', 'LSK1R')
  expect(lines(unit)[10]).toMatch(/80KT$/)
  enter(unit, 'RDG', 'LSK2L')
  enter(unit, '90', 'LSK3L')
  enter(unit, '5', 'LSK4L')

  unit.press('LSK6R')
  expect(titleLine(unit)).toMatch(/^MOD SQUARE SAR/)
  // 120 kt is faster than the 80 kt the 0.5 NM spacing allows.
  expect(scratch(unit)).toBe('HIGH SAR SPEED')
  expect(idents(unit)[0]).toBe('SQR01')
  unit.press('EXEC')
  expect(titleLine(unit)).toMatch(/^ACT SQUARE SAR/)
  expect(lines(unit)[10]).toMatch(/^SQUARE ARMED/)

  unit.sequence()
  expect(lines(unit)[10]).toMatch(/^SQUARE IN PROG/)
  unit.sequence()
  expect(idents(unit)[0]).toBe('SQR01')
  unit.press('LSK6R')
  expect(lines(unit)[10]).toMatch(/^NONE/)
  expect(idents(unit)[0]).toBe('MUN')
})

test('the civil SAR configuration keeps military tactical approaches off while civil hover remains available', () => {
  const { unit } = fms()
  press(unit, 'TACT', 'LSK3R')
  expect(titleLine(unit)).toMatch(/^DES\+SAR/)
  expect(scratch(unit)).toBe('NOT CONFIGURED')
  expect(unit.lamps().has('EXEC')).toBe(false)
  press(unit, 'CLR', 'LSK1R')
  expect(titleLine(unit)).toMatch(/^HOVER/)
})

test('an explicitly configured laboratory tactical approach puts IAF, FAF and MAP ahead of the route', () => {
  const profile = structuredClone(HELICOPTER_PROFILE)
  profile.id = 'lab-tactical-approach'
  profile.title = 'Laboratory tactical approach'
  profile.configuration.id = 'lab-tactical-approach'
  profile.configuration.options.tacticalApproach.configured = true
  profile.configuration.options.tacticalApproach.implementation = 'partial'
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14)), { profile })
  press(unit, 'TACT', 'LSK3R')
  expect(lines(unit)[12]).not.toContain('ACTIVATE>')
  enter(unit, 'FERDI', 'LSK1L')
  enter(unit, '-8', 'LSK5L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR')
  expect(lines(unit)[10]).toMatch(/-3\.0°\s+1230FT$/)
  unit.press('LSK6R')
  expect(titleLine(unit)).toMatch(/^MOD RTE 1 LEGS/)
  expect(idents(unit).slice(0, 5)).toEqual(['TIAF', 'TFAF', 'TMAP', '(disco)', 'MUN'])
})

test('the timer raises TIMER ALARM when the countdown runs out', () => {
  const { unit, advance } = fms()
  press(unit, 'INIT_REF', 'LSK5R')
  expect(titleLine(unit)).toMatch(/^TIMER/)
  enter(unit, '1', 'LSK1R')
  unit.press('LSK3L')
  expect(lines(unit)[2]).toMatch(/06:00$/)
  advance(359_000)
  unit.tick()
  expect(unit.lamps().has('MSG')).toBe(false)
  advance(1_000)
  unit.tick()
  expect(scratch(unit)).toBe('TIMER ALARM')
  expect(lines(unit)[2]).toMatch(/--:--$/)
})

test('FMS failure lights only FAIL, blanks the display and ignores keys; recovery restarts on IDENT', () => {
  const { unit } = fms()
  unit.press('RTE')
  unit.setCondition('gpsLost', true)
  unit.setCondition('dmeOutage', true)
  unit.setCondition('fmsFail', true)
  expect([...unit.lamps()]).toEqual(['FAIL'])
  expect(lines(unit).every(line => line.trim() === '')).toBe(true)
  unit.press('LEGS')
  unit.sequence()
  unit.setCondition('fmsFail', false)
  expect(titleLine(unit)).toMatch(/^IDENT/)
  expect(idents(unit)[0]).toBe('MUN')
  expect(unit.lamps().has('POS')).toBe(true)
})

test('GPS loss raises GPS NAV LOST and falls back to radio updating; with no DME either, the FMS dead reckons and POS lights', () => {
  const { unit } = fms()
  unit.setCondition('gpsLost', true)
  expect(scratch(unit)).toBe('GPS NAV LOST')
  // Near Ottawa at 3000 ft only the YOW VOR/DME is in range, so the FMS updates from it: no dead reckoning yet.
  expect(unit.lamps().has('POS')).toBe(false)
  press(unit, 'CLR', 'PROG')
  expect(lines(unit)[12]).toMatch(/^VOR\/DME/)
  expect(lines(unit)[10]).toMatch(/^1\.00\/0\.5\dNM/)
  unit.setCondition('dmeOutage', true)
  expect(unit.lamps().has('POS')).toBe(true)
  expect(lines(unit)[12]).toMatch(/^DR/)
  unit.press('NEXT')
  unit.press('NEXT')
  expect(lines(unit)[2]).toMatch(/^NO SIGNAL/)
})

test('RNP exceeded and NPA change RNP/ANP; the offset lamp follows an offset entered or deleted on PROGRESS 4/4', () => {
  const { unit } = fms()
  unit.setCondition('npa', true)
  unit.setCondition('rnpExceeded', true)
  expect(scratch(unit)).toBe('CHECK ANP')
  press(unit, 'CLR', 'PROG')
  expect(lines(unit)[10]).toMatch(/^0\.30\/1\.35NM/)
  expect(unit.lamps().has('RNP') && unit.lamps().has('NPA')).toBe(true)

  unit.press('PREV')
  enter(unit, 'L25', 'LSK1L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR')
  enter(unit, 'R1.5', 'LSK1L')
  expect(lines(unit)[2]).toMatch(/^R1\.5NM/)
  // The offset is a modification: OFST lights once it is executed, and goes out once its deletion is.
  expect(unit.lamps().has('OFST')).toBe(false)
  unit.press('EXEC')
  expect(unit.lamps().has('OFST')).toBe(true)
  press(unit, 'CLR', 'LSK1L')
  expect(unit.lamps().has('OFST')).toBe(true)
  unit.press('EXEC')
  expect(unit.lamps().has('OFST')).toBe(false)
  expect(unit.hasCondition('offset')).toBe(false)
})

test('ANS answers a GSM call, hangs it up on the next press, and shows the newest SMS', () => {
  const { unit, advance } = fms()
  unit.setCondition('gsmCall', true)
  unit.setCondition('sms', true)
  expect(unit.lamps().has('GSM') && unit.lamps().has('SMS')).toBe(true)
  unit.press('ANS')
  advance(32_000)
  expect(lines(unit)[2]).toMatch(/^IN CALL 00:32/)
  expect(lines(unit)[7]).toMatch(/^RETURN TO BASE AFTER/)
  expect(unit.lamps().has('SMS')).toBe(true)
  unit.press('ANS')
  expect(lines(unit)[2]).toMatch(/^NO CALL/)
  expect(unit.lamps().has('GSM')).toBe(false)
  unit.press('ANS')
  expect(unit.lamps().has('SMS')).toBe(false)
})

test('an ATC uplink lights ATC until answered, and WILCO to a direct-to loads it as a modification', () => {
  const { unit } = fms()
  unit.setCondition('atcUplink', true)
  unit.setCondition('atcUplink', false)
  unit.setCondition('atcUplink', true)
  expect(unit.lamps().has('ATC')).toBe(true)
  unit.press('ATC')
  expect(lines(unit)[2]).toMatch(/^PROCEED DIRECT TO TOLGU/)
  unit.press('LSK6R')
  expect(unit.lamps().has('ATC')).toBe(false)
  expect(unit.routeStatus).toBe('MOD')
  expect(idents(unit)[0]).toBe('TOLGU')
  unit.press('NEXT')
  expect(lines(unit)[1]).toMatch(/WILCO $/)
})

test('an FMC COMM route uplink loads as a modification', () => {
  const { unit } = fms()
  unit.press('FMC_COMM')
  expect(lines(unit)[8]).not.toContain('LOAD ROUTE>')
  unit.press('LSK1L')
  expect(scratch(unit)).toBe('ROUTE UPLINK RECEIVED')
  unit.press('LSK4R')
  expect(titleLine(unit)).toMatch(/^MOD RTE 1 LEGS/)
  expect(idents(unit)[0]).toBe('ELIBA')
})

test('the subsystem request lights MENU until the subsystem is selected on MCDU MENU', () => {
  const { unit } = fms()
  unit.setCondition('menuRequest', true)
  expect(unit.lamps().has('MENU')).toBe(true)
  unit.press('MENU')
  expect(lines(unit)[4]).toMatch(/REQ$/)
  unit.press('LSK2L')
  expect(unit.lamps().has('MENU')).toBe(false)
})

test('every function key on every hardware variation changes what the unit shows', () => {
  for (const variant of CDU_VARIANTS) {
    const fns = [...variant.rowTwo, 'MENU', 'INIT_REF', 'RTE', 'DEP_ARR', 'LEGS', 'PROG'] as CduFunction[]
    for (const fn of fns) {
      const { unit } = fms()
      const before = { screen: lines(unit).join('\n'), brightness: unit.brightness() }
      unit.press(fn)
      const changed = lines(unit).join('\n') !== before.screen || unit.brightness() !== before.brightness
      expect(changed, `${variant.id} ${fn}`).toBe(true)
    }
  }
})

test('the alert library holds displayable, distinct messages, and the simulation cannot raise one outside it', () => {
  expect(new Set(ALERTS.map(entry => entry.text)).size).toBe(ALERTS.length)
  for (const entry of ALERTS) expect(entry.text.length, entry.text).toBeLessThanOrEqual(COLUMNS)
  expect(() => alert('GPS NAV LOST')).not.toThrow()
  expect(() => alert('MADE UP ALERT')).toThrow()
})

test('display luminance follows ambient light and BRT, never leaving the mode range; NVG stays within 0.1 to 3 fL', () => {
  for (const { id } of LIGHTING_MODES) {
    const { min, max } = LUMINANCE_RANGE[id]
    for (const brt of [0, 0.5, 1]) for (const ambient of [0, 0.5, 1]) {
      const fl = displayLuminance(brt, { mode: id, ambient })
      expect(fl, `${id} ${brt} ${ambient}`).toBeGreaterThanOrEqual(min - 1e-9)
      expect(fl, `${id} ${brt} ${ambient}`).toBeLessThanOrEqual(max + 1e-9)
    }
  }
  expect(LUMINANCE_RANGE.nvg).toEqual({ min: 0.1, max: 3 })
  const at = (brt: number, ambient: number) => displayLuminance(brt, { mode: 'day', ambient })
  expect(at(0.5, 0.9)).toBeGreaterThan(at(0.5, 0.2))
  expect(at(0.9, 0.5)).toBeGreaterThan(at(0.2, 0.5))
})
