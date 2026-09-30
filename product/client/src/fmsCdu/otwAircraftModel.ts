/**
 * The chase view's helicopter model in the scene (helicopterModel.ts, served same-origin as
 * fms-cdu/models/helicopter-light-twin.glb; DEC-047): placed by the view's model matrix every frame, its main rotor
 * and tail fan turned by the rotor angle the view passes. Until it has loaded, or if it fails to, `ready` says so and
 * the view keeps its boxes-and-ellipsoids fallback (outTheWindow.ts AIRCRAFT_PARTS).
 */
export const HELICOPTER_MODEL_URL = "fms-cdu/models/helicopter-light-twin.glb";

/**
 * Rotor speeds, radians per second: a light twin's main rotor turns at about 395 rpm, its shrouded tail fan at about
 * 3,580 rpm (laboratory values of the class, not a type's data). At display frame rates the blades alias, as they do
 * on camera; the model's translucent disc stands for the blur.
 */
export const MAIN_ROTOR_RAD_S = (395 * 2 * Math.PI) / 60;
export const TAIL_ROTOR_RAD_S = (3580 * 2 * Math.PI) / 60;

/** The Cesium engine module (a stand-in in tests). */
export type CesiumModule = typeof import("@cesium/engine");
type ModelScene = { primitives: { add<T>(primitive: T): T; remove(primitive: unknown): boolean }; requestRender?: () => void };
type LoadedModel = Awaited<ReturnType<CesiumModule["Model"]["fromGltfAsync"]>>;
type ModelNode = NonNullable<ReturnType<LoadedModel["getNode"]>>;
type Matrix4 = InstanceType<CesiumModule["Matrix4"]>;

export type AircraftModel = {
  /** Places the model and turns its rotors; nothing until it has loaded. */
  update(modelMatrix: Matrix4 | undefined, rotorAngleRad: number, show: boolean): void;
  destroy(): void;
  /** Whether the model has loaded (the view hides its fallback then). */
  readonly loaded: boolean;
  readonly ready: Promise<{ loaded: true } | { failed: string }>;
};

/** The main rotor's angle to the tail fan's: the fan turns about 9 times faster. */
export const TAIL_TO_MAIN = TAIL_ROTOR_RAD_S / MAIN_ROTOR_RAD_S;

export function createAircraftModel(Cesium: CesiumModule, scene: ModelScene, url = `${import.meta.env.BASE_URL}${HELICOPTER_MODEL_URL}`): AircraftModel {
  let model: LoadedModel | null = null;
  let destroyed = false;
  let main: ModelNode | undefined, tail: ModelNode | undefined;
  let mainBase: Matrix4 | undefined, tailBase: Matrix4 | undefined;
  const ready = Cesium.Model.fromGltfAsync({ url, upAxis: Cesium.Axis.Z, forwardAxis: Cesium.Axis.X, show: false }).then(loaded => {
    if (destroyed) { loaded.destroy(); return { failed: "destroyed before the model loaded" } as const; }
    model = scene.primitives.add(loaded);
    return { loaded: true } as const;
  }, (error: unknown) => ({ failed: `helicopter model not loaded: ${error instanceof Error ? error.message : String(error)}` }) as const);
  const nodes = () => {
    if (!model || main) return;
    // The nodes are reachable once the model is ready (its first frames); their original matrices hold the hub places.
    main = model.getNode("main_rotor");
    tail = model.getNode("tail_rotor");
    if (main) mainBase = Cesium.Matrix4.clone(main.originalMatrix);
    if (tail) tailBase = Cesium.Matrix4.clone(tail.originalMatrix);
  };
  return {
    get loaded() { return model !== null; },
    ready,
    update(modelMatrix, rotorAngleRad, show) {
      if (!model) return;
      model.show = show;
      if (!show || !modelMatrix) return;
      model.modelMatrix = modelMatrix;
      nodes();
      if (main && mainBase) main.matrix = Cesium.Matrix4.multiply(mainBase, Cesium.Matrix4.fromRotation(Cesium.Matrix3.fromRotationZ(rotorAngleRad)), new Cesium.Matrix4());
      if (tail && tailBase) tail.matrix = Cesium.Matrix4.multiply(tailBase, Cesium.Matrix4.fromRotation(Cesium.Matrix3.fromRotationY(rotorAngleRad * TAIL_TO_MAIN)), new Cesium.Matrix4());
    },
    destroy() {
      destroyed = true;
      if (model) scene.primitives.remove(model);
      model = null;
    },
  };
}
