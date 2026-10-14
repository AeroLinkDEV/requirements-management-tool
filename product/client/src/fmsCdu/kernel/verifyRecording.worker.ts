import { currentRecordingIdentity, verifyRecording, type Reproduction } from "./recording";

// A fresh module worker owns the replay's units/stores. It never receives a live kernel or browser Storage.
self.onmessage = async (event: MessageEvent<Reproduction>) => {
  try { self.postMessage({ ok: true, result: await verifyRecording(event.data, await currentRecordingIdentity()) }); }
  catch (error) { self.postMessage({ ok: false, error: error instanceof Error ? error.message : String(error) }); }
};
