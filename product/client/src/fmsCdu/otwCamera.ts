import type * as Engine from "@cesium/engine";
import type { CameraPose } from "./outTheWindow";

/** The simulation owns this camera; identical aircraft poses must leave its view fixed. */
export function aircraftCamera(Cesium: typeof Engine, camera: Engine.Camera) {
  let previous = "";
  const position = new Cesium.Cartesian3(), direction = new Cesium.Cartesian3(), up = new Cesium.Cartesian3(), right = new Cesium.Cartesian3();
  return (pose: CameraPose) => {
    const key = `${pose.longitude} ${pose.latitude} ${pose.height} ${pose.heading} ${pose.pitch} ${pose.roll}`;
    if (key !== previous) {
      previous = key;
      camera.setView({
        destination: Cesium.Cartesian3.fromDegrees(pose.longitude, pose.latitude, pose.height),
        orientation: { heading: pose.heading, pitch: pose.pitch, roll: pose.roll },
      });
      Cesium.Cartesian3.clone(camera.position, position);
      Cesium.Cartesian3.clone(camera.direction, direction);
      Cesium.Cartesian3.clone(camera.up, up);
      Cesium.Cartesian3.clone(camera.right, right);
    } else {
      // Cesium's heading/roll reads transform these vectors out and back during initializeFrame. Restore
      // the aircraft-owned basis before its render predicate observes that rounding drift as motion.
      // Copy the vectors rather than calling setView again (which itself repeats the transform round trip).
      Cesium.Cartesian3.clone(position, camera.position);
      Cesium.Cartesian3.clone(direction, camera.direction);
      Cesium.Cartesian3.clone(up, camera.up);
      Cesium.Cartesian3.clone(right, camera.right);
    }
  };
}
