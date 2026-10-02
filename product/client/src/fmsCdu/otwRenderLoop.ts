type Options = {
  window: Window;
  canvas: HTMLCanvasElement;
  unavailable: () => boolean;
  /** Resize and run the engine, returning whether its public postRender event advanced. */
  render: () => boolean;
  onFailure: (error: unknown) => void;
};

/** Admit one draw at a time without waiting on the CPU or advancing the simulation clock. */
export function aircraftRenderLoop({ window: destination, canvas, unavailable, render, onFailure }: Options) {
  let gl: WebGL2RenderingContext | null = null;
  try { gl = canvas.getContext("webgl2"); } catch { /* Unsupported contexts retain the normal render loop. */ }
  let pending: WebGLSync | null = null;
  const release = () => {
    const sync = pending;
    pending = null;
    if (sync && gl) {
      try { gl.deleteSync(sync); } catch { /* A lost context must not prevent teardown or fallback. */ }
    }
  };
  const disableGuard = () => { release(); gl = null; };
  const canRender = () => {
    if (!gl) return true;
    try {
      if (gl.isContextLost()) { disableGuard(); return true; }
      if (!pending) return true;
      // Zero timeout polls only: an unfinished draw postpones renderer admission, not input or ticks.
      const status = gl.clientWaitSync(pending, 0, 0);
      if (status === gl.TIMEOUT_EXPIRED) return false;
      if (status === gl.ALREADY_SIGNALED || status === gl.CONDITION_SATISFIED) release();
      else disableGuard(); // WAIT_FAILED (or an unsupported result) cannot freeze the view.
    } catch { disableGuard(); }
    return true;
  };
  const submitted = () => {
    if (!gl || pending) return;
    try {
      if (gl.isContextLost()) { disableGuard(); return; }
      pending = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
      if (!pending) { disableGuard(); return; }
      // Make the fence eligible to complete before the next RAF without finish/waitSync/readback.
      gl.flush();
    } catch { disableGuard(); }
  };

  let renderFrame = 0, lastFrame = 0, stopped = false, destroyed = false, pageHidden = false;
  const stop = () => { stopped = true; destination.cancelAnimationFrame(renderFrame); renderFrame = 0; };
  const hidePage = () => { pageHidden = true; stop(); }; // Same context: retain ownership of its pending fence.
  const showPage = () => {
    if (!pageHidden || destroyed || unavailable() || destination.closed) return;
    pageHidden = false;
    stopped = false;
    lastFrame = 0;
    if (!renderFrame) renderFrame = destination.requestAnimationFrame(draw);
  };
  const destroy = () => {
    if (destroyed) return;
    destroyed = true;
    stop();
    disableGuard();
    destination.removeEventListener("pagehide", hidePage);
    destination.removeEventListener("pageshow", showPage);
  };
  const draw = (at: number) => {
    renderFrame = 0;
    if (stopped) return;
    if (unavailable() || destination.closed) { destroy(); return; }
    try {
      const elapsed = at - lastFrame, interval = 1000 / 30;
      if (elapsed >= interval && canRender()) {
        const advanced = render();
        // Render errors can synchronously destroy this loop from the engine's renderError event.
        if (stopped) return;
        if (unavailable() || destination.closed) { destroy(); return; }
        if (advanced) submitted();
        lastFrame = at - elapsed % interval;
      }
      if (!stopped) renderFrame = destination.requestAnimationFrame(draw);
    } catch (error) { destroy(); onFailure(error); }
  };
  destination.addEventListener("pagehide", hidePage);
  destination.addEventListener("pageshow", showPage);
  renderFrame = destination.requestAnimationFrame(draw);
  return { destroy };
}
