import { caption, dashes, hhmm, isOutstanding, medium, prompt, simulated, small, title, wrap, type DatalinkPageId, type Leg, type Page } from "./fmsModel";
import type { Line } from "./screen";

/**
 * Datalink pages: ATC (CPDLC), FMC COMM and the GSM/SMS page behind ANS. The Operator's Manual describes the keys
 * and annunciators but this simulation has no datalink, so these pages are representative and say so on screen.
 */

export const DEMO_UPLINKS: readonly string[] = [
  "CLIMB TO AND MAINTAIN FL150",
  "PROCEED DIRECT TO TOLGU",
  "CONTACT MONTREAL CENTER 132.45",
  "DESCEND TO AND MAINTAIN 4000FT",
  "REPORT PASSING RDG",
];

export const DEMO_SMS: readonly { from: string; text: string }[] = [
  { from: "+1 613 555 0199", text: "RETURN TO BASE AFTER SEARCH LEG THREE" },
  { from: "+1 514 555 0110", text: "FUEL TRUCK READY AT PAD 4" },
];

/** The route FMC COMM "receives" for a route request: a different arrival into the same approach. */
const UPLINKED_ROUTE: Leg[] = [
  { kind: "wpt", ident: "ELIBA", altitude: "5000" }, { kind: "wpt", ident: "KILLA", altitude: "4500" },
  { kind: "wpt", ident: "AGBEK", altitude: "3000" }, { kind: "wpt", ident: "FERDI", altitude: "1500A" },
  { kind: "wpt", ident: "RW24R", altitude: "168" }, { kind: "wpt", ident: "CYUL" },
];

const utc = (date: Date) => hhmm(date).slice(0, 4);

export const DATALINK_PAGES: Record<DatalinkPageId, Page> = {
  ATC: {
    pages: () => 2,
    render: (fms, index) => {
      const uplinks = fms.uplinks;
      if (index === 1) {
        const lines: (Line | undefined)[] = [title("ATC LOG", "2/2")];
        uplinks.slice(0, 5).forEach((uplink, i) => {
          lines[1 + i * 2] = caption(` ${utc(uplink.at)}Z CZUL`, `${uplink.response} `);
          lines[2 + i * 2] = { left: medium(wrap(uplink.text)[0] ?? "") };
        });
        if (!uplinks.length) lines[2] = { center: medium("NO MESSAGES") };
        lines[12] = { center: simulated("SIMULATED DATALINK").center };
        return lines;
      }
      const current = uplinks.find(isOutstanding);
      const lines: (Line | undefined)[] = [title("ATC UPLINK", "1/2")];
      if (!current) {
        lines[2] = { center: medium("NO OPEN UPLINK") };
      } else {
        lines[1] = caption(` ${utc(current.at)}Z FROM CZUL`, `${current.response} `);
        wrap(current.text).slice(0, 4).forEach((text, i) => { lines[2 + i] = { left: { text } }; });
        lines[10] = { left: prompt("<STANDBY") };
        lines[11] = { left: dashes(24) };
        lines[12] = { left: prompt("<UNABLE"), right: prompt("WILCO>") };
      }
      lines[8] = simulated("SIMULATED DATALINK");
      return lines;
    },
    lsk: (fms, side, row, _scratch, index) => {
      if (index !== 0) return;
      const current = fms.uplinks.find(isOutstanding);
      if (!current) return;
      if (side === "L" && row === 5) current.response = "STANDBY";
      if (side === "L" && row === 6) current.response = "UNABLE";
      if (side === "R" && row === 6) {
        // A loadable clearance: WILCO to a direct-to puts it in the route as a modification to execute.
        const direct = /^PROCEED DIRECT TO ([A-Z0-9]{2,5})$/.exec(current.text);
        if (direct) {
          const result = fms.directTo(direct[1]);
          if (result) return result;
        }
        current.response = "WILCO";
      }
    },
  },

  FMC_COMM: {
    pages: () => 1,
    render: fms => {
      const d = fms.datalink;
      return [
        title("FMC COMM", "1/1"),
        caption(" ROUTE REQUEST", "STATUS "),
        { left: prompt("<SEND"), right: medium(d.routeRequest, d.routeRequest === "NONE" ? "white" : "green") },
        caption(" WIND REQUEST", "STATUS "),
        { left: prompt("<SEND"), right: medium(d.windRequest, d.windRequest === "NONE" ? "white" : "green") },
        caption(" POSITION REPORT", "SENT "),
        { left: prompt("<SEND"), right: medium(d.posReport ? `${utc(d.posReport)}Z` : "-----") },
        undefined,
        { right: d.routeRequest === "RECEIVED" ? prompt("LOAD ROUTE>") : undefined },
        undefined,
        simulated("SIMULATED DATALINK"),
        { left: dashes(24) },
        { left: prompt("<INDEX") },
      ];
    },
    lsk: (fms, side, row) => {
      const d = fms.datalink;
      if (side === "L") {
        switch (row) {
          case 1: d.routeRequest = "RECEIVED"; fms.advisory("ROUTE UPLINK RECEIVED"); return;
          case 2: d.windRequest = "RECEIVED"; fms.advisory("WIND UPLINK RECEIVED"); return;
          case 3: d.posReport = fms.now; return;
          case 6: fms.open("INIT_REF", 1); return;
        }
        return;
      }
      if (row === 4 && d.routeRequest === "RECEIVED") {
        fms.replaceLegs(UPLINKED_ROUTE);
        d.routeRequest = "LOADED";
        fms.open("LEGS");
      }
    },
  },

  ANS: {
    pages: () => 1,
    render: fms => {
      const call = fms.callState;
      const seconds = Math.max(0, Math.floor((fms.now.getTime() - call.since) / 1000));
      const duration = `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
      const sms = fms.smsList[0];
      const lines: (Line | undefined)[] = [
        title("GSM/SMS", "1/1"),
        caption(" CALL"),
        call.state === "ringing" ? { left: { text: "INCOMING CALL", color: "amber" } }
          : call.state === "active" ? { left: { text: `IN CALL ${duration}`, color: "green" } }
            : { left: medium("NO CALL") },
        call.state === "none" ? undefined : caption(" FROM"),
        call.state === "none" ? undefined : { left: medium(call.from) },
        caption(sms ? ` SMS ${utc(sms.at)}Z` : " SMS"),
      ];
      if (sms) {
        lines[6] = { left: small(sms.from, "green") };
        wrap(sms.text).slice(0, 3).forEach((text, i) => { lines[7 + i] = { left: medium(text) }; });
      } else lines[6] = { left: medium("NO MESSAGE") };
      lines[10] = simulated("SIMULATED GSM");
      lines[11] = { left: dashes(24) };
      lines[12] = call.state === "ringing" ? { left: prompt("<ANSWER") } : call.state === "active" ? { left: prompt("<HANG UP") } : undefined;
      return lines;
    },
    lsk: (fms, side, row) => {
      if (side !== "L" || row !== 6) return;
      if (fms.callState.state === "ringing") fms.answerCall();
      else if (fms.callState.state === "active") fms.endCall();
    },
  },
};
