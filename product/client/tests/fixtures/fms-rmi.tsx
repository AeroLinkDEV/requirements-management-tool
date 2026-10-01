import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import { aircraftData, fmsOutputs, type Word } from '../../src/fmsCdu/efis'
import { Nd } from '../../src/fmsCdu/FmsEfis'
import { FlightSimulator } from '../../src/fmsCdu/flight'
import { ScriptedFms } from '../../src/fmsCdu/scriptedFms'

// Render the real EFIS from a detached output snapshot. Deliberately disagreeing heading and track catches a
// track-up rendering of heading-relative ADF data. No live FMS instance is passed to the display.
const unit = new ScriptedFms()
const sim = new FlightSimulator(unit)
const snapshot = fmsOutputs(unit, sim)
const air = { ...aircraftData(unit, sim), heading: 120, track: 290 }
const good: Word<number> = { value: 90, status: 'NORMAL' }
function Fixture() {
  const [bearing, setBearing] = useState(good)
  const bus = { ...snapshot, angleReference: 'TRUE' as const,
    radioMeasurements: { ...snapshot.radioMeasurements, adf: { adfBearing: bearing }, adf2: { adfBearing: { value: 315, status: 'NORMAL' as const } } } }
  return <>
    <button onClick={() => setBearing(good)}>Valid bearing</button>
    <button onClick={() => setBearing({ value: null, status: 'NCD' })}>NDB off air</button>
    <button onClick={() => setBearing({ value: null, status: 'FAIL' })}>Receiver failed</button>
    <button onClick={() => setBearing({ value: null, status: 'FAIL' })}>Measurement bus lost</button>
    <div style={{ width: 420 }}><Nd bus={bus} air={air} range={20} /></div>
  </>
}
createRoot(document.getElementById('root')!).render(<Fixture />)
