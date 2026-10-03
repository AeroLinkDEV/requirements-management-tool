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
  /** Places a decoded model; turns its rotors only after Cesium's GPU readiness boundary. */
  update(modelMatrix: Matrix4 | undefined, rotorAngleRad: number, show: boolean): void;
  destroy(): void;
  /** Whether the model is render-ready (the view hides its fallback then). */
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
  const rotorRotation = new Cesium.Matrix3();
  const mainTransform = new Cesium.Matrix4(), tailTransform = new Cesium.Matrix4();
  let removeReady: (() => void) | undefined, removeError: (() => void) | undefined;
  let settle: ((result: { loaded: true } | { failed: string }) => void) | undefined;
  const ready: AircraftModel["ready"] = new Promise(resolve => { settle = resolve; });
  const detach = () => { removeReady?.(); removeError?.(); removeReady = removeError = undefined; };
  const finish = (result: { loaded: true } | { failed: string }) => {
    detach();
    settle?.(result);
    settle = undefined;
  };
  const failed = (error: unknown) => {
    finish({ failed: `helicopter model not loaded: ${error instanceof Error ? error.message : String(error)}` });
    // Cesium can emit errorEvent during Model.update and continue using the model on that stack.
    // Keep the fallback immediately, but release GPU resources only once that update has unwound.
    const failedModel = model;
    model = null;
    if (failedModel) void Promise.resolve().then(() => { scene.primitives.remove(failedModel); });
    if (!destroyed) scene.requestRender?.();
  };
  void Cesium.Model.fromGltfAsync({
    url, upAxis: Cesium.Axis.Z, forwardAxis: Cesium.Axis.X, show: false,
    environmentMapOptions: { enabled: false },
  }).then(loaded => {
    if (destroyed) { loaded.destroy(); return; }
    // File decoding finishes before GPU initialization. The primitive must enter the scene so its first
    // render can initialize it; waiting for ready before adding it would prevent that render entirely.
    model = scene.primitives.add(loaded);
    const rendered = () => {
      if (destroyed || model !== loaded || !loaded.ready) return;
      finish({ loaded: true });
      scene.requestRender?.();
    };
    removeReady = loaded.readyEvent.addEventListener(rendered);
    removeError = loaded.errorEvent.addEventListener(failed);
    scene.requestRender?.();
    rendered(); // Also support a model whose GPU initialization has already completed.
  }).catch(failed);
  const nodes = () => {
    if (!model?.ready || main) return;
    // The nodes are reachable once the model is ready (its first frames); their original matrices hold the hub places.
    main = model.getNode("main_rotor");
    tail = model.getNode("tail_rotor");
    if (main) mainBase = Cesium.Matrix4.clone(main.originalMatrix);
    if (tail) tailBase = Cesium.Matrix4.clone(tail.originalMatrix);
  };
  return {
    get loaded() { return model?.ready === true; },
    ready,
    update(modelMatrix, rotorAngleRad, show) {
      if (!model) return;
      model.show = show;
      // Cesium updates the environment map before checking Model.show. Request lighting work only
      // for a placed, visible aircraft; disabling it retains its maps but cannot cancel queued GPU work.
      model.environmentMapManager.enabled = show && !!modelMatrix;
      if (!show || !modelMatrix) return;
      model.modelMatrix = modelMatrix;
      if (!model.ready) return;
      nodes();
      // Node setters copy the transform. Keep each rotor's result separate and its original hub unchanged.
      if (main && mainBase) main.matrix = Cesium.Matrix4.multiplyByMatrix3(mainBase, Cesium.Matrix3.fromRotationZ(rotorAngleRad, rotorRotation), mainTransform);
      if (tail && tailBase) tail.matrix = Cesium.Matrix4.multiplyByMatrix3(tailBase, Cesium.Matrix3.fromRotationY(rotorAngleRad * TAIL_TO_MAIN, rotorRotation), tailTransform);
    },
    destroy() {
      destroyed = true;
      finish({ failed: "destroyed before the model was render-ready" });
      if (model) scene.primitives.remove(model);
      model = null;
    },
  };
}
