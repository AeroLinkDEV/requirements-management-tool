import { useState } from "react";
import type { FmsSide } from "./crossTalk";
import type { Submit } from "./kernel/submission";
import type { FmsView } from "./scriptedFms";
import type { ScenarioRecorder } from "./scenario";
import { describeSensorStimulus, type SensorStimulus } from "./sensorStimulus";
import { RADIO_NAMES, type DmeDevice, type RadioDevice, type RadioFaults } from "./radioManagement";

/**
 * Real sensor/world controls. Inputs and the recorded power target are FMS1; physical radios and stations are shared.
 * Each stimulus is submitted to the kernel (`f14.sensor`) on the computer it addresses; the card shows its outcome.
 */
export default function FmsSensorFaultCard({ backend, side, sensorOwner, recordTo, submit }: {
  backend: FmsView; side: FmsSide; sensorOwner: FmsView; recordTo: ScenarioRecorder | null; submit: Submit;
}) {
  const [device, setDevice] = useState<RadioDevice | DmeDevice>("nav1");
  const [receiver, setReceiver] = useState<RadioFaults["receiver"]>("FAILED");
  const [path, setPath] = useState<RadioFaults["controlPath"]>("LOST");
  const [measurement, setMeasurement] = useState<RadioFaults["measurementBus"]>("LOST");
  const [station, setStation] = useState("");
  const [world, setWorld] = useState("DME_NO_REPLY");
  const [reportedIdent, setReportedIdent] = useState("BAD");
  const [radialBias, setRadialBias] = useState("10");
  const [headingBias, setHeadingBias] = useState("10");
  const [duration, setDuration] = useState("51");
  const [dvsSurface, setDvsSurface] = useState<"LAND" | "SEA">("LAND");
  const [apirsNorth, setApirsNorth] = useState("0.3");
  const [apirsEast, setApirsEast] = useState("0");
  const [notice, setNotice] = useState("No sensor stimulus applied.");
  const apply = (action: SensorStimulus) => {
    const ownsInputs = action.kind === "airInput" || action.kind === "dvsInput" || action.kind === "gpsPair" || action.kind === "powerInterrupt" || action.kind === "apirsBias";
    const owner = ownsInputs ? sensorOwner : backend;
    submit({ kind: "f14.sensor", unit: ownsInputs ? 1 : side, stimulus: action }, { kind: "f14", id: `sensor.${action.kind}` }, event => {
      if (event.outcome.status === "refused") { setNotice(`Refused: ${event.outcome.reason}`); return; }
      recordTo?.sensor(action);
      setNotice(`${owner.utcTime.toISOString()}: ${describeSensorStimulus(action)}.`);
    });
  };
  const faults = backend.radioPort?.faults(device);
  const input = sensorOwner.navigationInputs;
  const doppler = sensorOwner.dvsStatus;
  const tacan = backend.tacanBearingAndRange();
  const observation = input?.radios.find(entry => entry.station.ident === station);
  const number = (value: number | null | undefined) => value === null || value === undefined ? "—" : value.toFixed(2);
  return <section className="fmsBenchCard fmsSensorFaultCard" aria-label="Sensor fault laboratory">
    <h2>Sensor fault laboratory</h2>
    <p className="fmsBenchHint">Laboratory input words and ground stations. FMS1 supplies both computers' sensor inputs; radio/world faults are shared even with cross-talk lost. Recorded power targets FMS1. Aircraft and AFCS truth remain separate from navigation TAS/heading inputs.</p>
    <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); apply({ kind: "radioFault", device, receiver }); }}>
      <label>Radio <select aria-label="Fault radio" value={device} onChange={event => setDevice(event.target.value as typeof device)}>
        {(["nav1", "nav2", "dme1", "dme2", "tacan", "adf", "adf2"] as const).map(id => <option key={id} value={id}>{RADIO_NAMES[id]}</option>)}
      </select></label>
      <label>Receiver <select aria-label="Radio receiver state" value={receiver} onChange={event => setReceiver(event.target.value as typeof receiver)}>{["NORMAL", "FAILED", "SILENT"].map(state => <option key={state}>{state}</option>)}</select></label>
      <button type="submit">Apply receiver state</button>
    </form>
    <div className="fmsBenchAlert">
      <label>Control path <select aria-label="Radio control path" value={path} onChange={event => setPath(event.target.value as typeof path)}>{["NORMAL", "LOST"].map(state => <option key={state}>{state}</option>)}</select></label>
      <button type="button" disabled={device === "dme1" || device === "dme2"} onClick={() => apply({ kind: "radioFault", device, controlPath: path })}>Apply control path</button>
      <label>Measurement bus <select aria-label="Radio measurement bus" value={measurement} onChange={event => setMeasurement(event.target.value as typeof measurement)}>{["NORMAL", "LOST"].map(state => <option key={state}>{state}</option>)}</select></label>
      <button type="button" onClick={() => apply({ kind: "radioFault", device, measurementBus: measurement })}>Apply measurement bus</button>
    </div>
    {device === "dme1" || device === "dme2" ? <p className="fmsBenchHint">DME tuning follows its paired NAV receiver. An independent DME control path fault is not supported; receiver and measurement bus faults remain available.</p> : null}
    <p className="fmsBenchReadout" data-testid="sensor-radio-readout">{RADIO_NAMES[device]}: receiver {faults?.receiver}, control {faults?.controlPath}, bus {faults?.measurementBus}; reported frequency {device === "dme1" || device === "dme2" ? backend.radioPort?.dmeReceiving(device) ? backend.dmeStation(device)?.frequency ?? "none" : "none" : backend.radioReceiving(device) ?? "none"}.
      {device === "nav1" || device === "nav2" ? ` Radial ${number(backend.navRadial(device))}°.` : device === "dme1" || device === "dme2" ? ` Range ${number(backend.dmeSlantRangeNm(device))} NM; ident ${backend.dmeReportedIdent(device) ?? "none"}.` : device === "adf" || device === "adf2" ? ` Relative bearing ${number(backend.adfRelativeBearing(device))}°.` : ` TACAN station ${backend.tacanStation()?.ident ?? "none"}; paired navigation measurements ${tacan ? `bearing ${number(tacan.bearing)}°, range ${number(tacan.rangeNm)} NM` : "unavailable"}.`}</p>
    <form className="fmsBenchAlert" onSubmit={event => {
      event.preventDefault();
      const ident = station.trim().toUpperCase();
      const action: SensorStimulus = world === "NDB_OFF" || world === "NDB_ON" ? { kind: "ndb", ident, offAir: world === "NDB_OFF" }
        : world === "OFF_AIR" || world === "ON_AIR" ? { kind: "stationOffAir", ident, off: world === "OFF_AIR" }
          : world === "VOR_BIAS" ? { kind: "stationFault", ident, component: "VOR", biasDeg: Number(radialBias) }
            : world === "DME_IDENT" ? { kind: "stationFault", ident, component: "DME", reportedIdent: reportedIdent.trim().toUpperCase() || null }
              : { kind: "stationFault", ident, component: "DME", reply: world === "DME_REPLY" };
      apply(action);
    }}>
      <label>Station ident <input aria-label="Fault station ident" value={station} onChange={event => setStation(event.target.value.toUpperCase())} maxLength={world === "NDB_OFF" || world === "NDB_ON" ? 7 : 4} /></label>
      <label>World stimulus <select aria-label="Ground station stimulus" value={world} onChange={event => setWorld(event.target.value)}>
        <option value="DME_NO_REPLY">DME stops replying</option><option value="DME_REPLY">DME replies</option><option value="DME_IDENT">DME reported ident</option>
        <option value="VOR_BIAS">VOR radial bias</option><option value="OFF_AIR">Facility off air</option><option value="ON_AIR">Facility on air</option>
        <option value="NDB_OFF">NDB off air</option><option value="NDB_ON">NDB on air</option>
      </select></label>
      {world === "DME_IDENT" ? <label>Reported ident (blank restores)<input aria-label="DME reported ident" value={reportedIdent} onChange={event => setReportedIdent(event.target.value.toUpperCase())} maxLength={4} /></label> : null}
      {world === "VOR_BIAS" ? <label>Bias degrees<input type="number" aria-label="VOR radial bias degrees" min={-180} max={180} value={radialBias} onChange={event => setRadialBias(event.target.value)} required /></label> : null}
      <button type="submit">Apply ground stimulus</button>
    </form>
    <p className="fmsBenchReadout" data-testid="sensor-world-readout">{observation ? `${observation.station.ident}: range ${observation.slantRangeNm.status} ${number(observation.slantRangeNm.value)} NM; reported ident ${observation.reportedDmeIdent?.value ?? "none"}; bearing ${observation.bearingTrue.status} ${number(observation.bearingTrue.value)}°.` : "No tuned VOR/DME observation for this ident. NDB bearings are in the ADF readout and RMI."}</p>
    <div className="fmsBenchAlert">
      <label><input type="checkbox" aria-label="Navigation TAS valid" checked={sensorOwner.airInputStimulus.tasValid} onChange={event => apply({ kind: "airInput", tasValid: event.target.checked })} />Navigation TAS valid</label>
      <label><input type="checkbox" aria-label="Navigation heading valid" checked={sensorOwner.airInputStimulus.headingValid} onChange={event => apply({ kind: "airInput", headingValid: event.target.checked })} />Navigation heading valid</label>
    </div>
    <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); apply({ kind: "airInput", headingBiasDeg: Number(headingBias) }); }}>
      <label>Navigation heading bias<input type="number" aria-label="Navigation heading bias degrees" min={-180} max={180} value={headingBias} onChange={event => setHeadingBias(event.target.value)} required /></label><button type="submit">Apply heading bias</button>
    </form>
    <p className="fmsBenchReadout" data-testid="sensor-air-readout">Measured navigation TAS {number(input?.air.value?.tasKt)} kt ({input?.air.value?.tasValid === false ? "invalid" : "valid"}); heading {number(input?.air.value?.headingTrue)}° ({input?.air.value?.headingValid === false ? "invalid" : "valid"}). Physical heading {number(sensorOwner.heading)}°. APIRS {input?.apirs?.status ?? "NCD"}; DVS {input?.dvs?.status ?? "NCD"}; navigation {sensorOwner.navPerformance.sensor.mode}.</p>
    <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); apply({ kind: "dvsInput", surface: dvsSurface }); }}>
      <label>Measured Doppler surface<select aria-label="Measured Doppler surface" value={dvsSurface} onChange={event => setDvsSurface(event.target.value as typeof dvsSurface)}><option value="LAND">LAND</option><option value="SEA">SEA</option></select></label>
      <button type="submit">Apply Doppler surface</button>
    </form>
    <p className="fmsBenchReadout" data-testid="sensor-dvs-readout">Doppler {doppler.mode}; VX {number(doppler.vxKt)} kt, VY {number(doppler.vyKt)} kt; sample {doppler.at === null ? "none" : new Date(doppler.at).toISOString()}; status {input?.dvs?.status ?? "NCD"}; source {doppler.source}. Crew water current applies only to a usable SEA word.</p>
    <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); apply({ kind: "powerInterrupt", durationMs: Number(duration) }); }}>
      <label>Power interruption ms<input type="number" aria-label="Power interruption duration ms" min={0} max={3_600_000} value={duration} onChange={event => setDuration(event.target.value)} required /></label><button type="submit">Interrupt KALMAN power</button>
    </form>
    <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); apply({ kind: "apirsBias", northMs2: Number(apirsNorth), eastMs2: Number(apirsEast) }); }}>
      <label>APIRS bias north m/s²<input type="number" aria-label="APIRS accelerometer bias north m/s²" min={-10} max={10} step={0.01} value={apirsNorth} onChange={event => setApirsNorth(event.target.value)} required /></label>
      <label>east m/s²<input type="number" aria-label="APIRS accelerometer bias east m/s²" min={-10} max={10} step={0.01} value={apirsEast} onChange={event => setApirsEast(event.target.value)} required /></label>
      <button type="submit">Apply APIRS bias</button>
    </form>
    <p className="fmsBenchHint">An APIRS accelerometer bias outside the nominal model (C2): KALMAN coasts on it without knowing; 0 and 0 clears it.</p>
    <p className="fmsBenchReadout" data-testid="sensor-power-readout">FMS1 KALMAN available: {sensorOwner.sensorSolutions.some(solution => solution.mode === "KALMAN" && solution.available) ? "yes" : "no"}.</p>
    <p className="fmsBenchHint">C2 models the KALMAN rule only: more than 50 ms restarts its one-minute warm-up. Use Nav data for a full computer power cycle.</p>
    <div className="fmsBenchAlert">{(["NORMAL", "INTEGRITY_ONLY", "POSITION_GONE"] as const).map(mode => <button type="button" key={mode} onClick={() => apply({ kind: "gpsPair", mode })}>{mode === "NORMAL" ? "Restore GPS pair words" : mode === "INTEGRITY_ONLY" ? "GPS pair integrity only" : "GPS pair position gone"}</button>)}</div>
    <p className="fmsBenchReadout" data-testid="sensor-gps-readout">{input?.gps.map((word, index) => `GPS${index + 1}: position ${word.value?.["110"].ssm ?? "NCD"}, HIL ${word.value?.["130"].ssm ?? "NCD"}`).join("; ")}. Pair presets replace overrides of HIL and the four position words; other receiver stimuli remain.</p>
    <p className="fmsBenchHint">The default profile has no external radio control head (DEC-150); a scenario injecting one is refused.</p>
    <p role="status" data-testid="sensor-stimulus-result">{notice}</p>
  </section>;
}
