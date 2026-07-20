const MIN_PANEL_PX = 220;
const STORAGE_PREFIX = "aerolink-resizable-layout:";
const ENHANCED = "data-resizable-enhanced";
const PANEL_COUNT = "data-resizable-panel-count";

const layoutTargets = [
  { selector: ".commandCenterPage > .grid", axis: "horizontal", key: "command-center-main" },
  { selector: ".reqWorkspace > .reqLayout", axis: "horizontal", key: "requirements-explorer-main" },
  { selector: "[data-resizable-layout='horizontal']", axis: "horizontal" },
  { selector: "[data-resizable-layout='vertical']", axis: "vertical" },
];

function directPanels(container) {
  return Array.from(container.children).filter(
    (element) => !element.classList.contains("workspaceSplitter"),
  );
}

function storageKey(container, target) {
  const explicit = container.getAttribute("data-resizable-key");
  const identity = explicit || target.key || container.id || container.className || target.selector;
  return `${STORAGE_PREFIX}${location.pathname}:${identity}`;
}

function equalSizes(count) {
  return Array.from({ length: count }, () => 100 / count);
}

function loadSizes(key, count) {
  try {
    const value = JSON.parse(localStorage.getItem(`${key}:${count}`) || "null");
    if (
      Array.isArray(value) &&
      value.length === count &&
      value.every((item) => Number.isFinite(item) && item > 0)
    ) {
      const total = value.reduce((sum, item) => sum + item, 0);
      return value.map((item) => (item / total) * 100);
    }
  } catch {
    // Corrupt or unavailable storage should never block the workspace.
  }
  return equalSizes(count);
}

function saveSizes(key, sizes) {
  try {
    localStorage.setItem(`${key}:${sizes.length}`, JSON.stringify(sizes));
  } catch {
    // Persistence is an enhancement; dragging still works without storage.
  }
}

function applySizes(container, axis, sizes) {
  const template = sizes.map((size) => `minmax(0, ${size}fr)`).join(" ");
  container.style.setProperty(
    axis === "horizontal" ? "grid-template-columns" : "grid-template-rows",
    template,
  );
  container.style.setProperty(
    axis === "horizontal" ? "grid-template-rows" : "grid-template-columns",
    axis === "horizontal" ? "" : "minmax(0, 1fr)",
  );
}

function handlePosition(sizes, boundary) {
  return sizes.slice(0, boundary + 1).reduce((sum, item) => sum + item, 0);
}

function positionHandles(container, axis, sizes) {
  container.querySelectorAll(":scope > .workspaceSplitter").forEach((handle) => {
    const boundary = Number(handle.dataset.boundary);
    const position = handlePosition(sizes, boundary);
    handle.style.setProperty(axis === "horizontal" ? "left" : "top", `${position}%`);
  });
}

function resizeBoundary(container, panels, axis, sizes, boundary, deltaPx) {
  const rect = container.getBoundingClientRect();
  const totalPx = axis === "horizontal" ? rect.width : rect.height;
  if (!totalPx) return sizes;

  const pairPercent = sizes[boundary] + sizes[boundary + 1];
  const pairPx = (pairPercent / 100) * totalPx;
  const currentPx = (sizes[boundary] / 100) * totalPx;
  const minimum = Math.min(MIN_PANEL_PX, Math.max(96, pairPx * 0.25));
  const nextPx = Math.min(pairPx - minimum, Math.max(minimum, currentPx + deltaPx));
  const next = [...sizes];
  next[boundary] = (nextPx / totalPx) * 100;
  next[boundary + 1] = pairPercent - next[boundary];

  applySizes(container, axis, next);
  positionHandles(container, axis, next);
  panels.forEach((panel) => panel.dispatchEvent(new CustomEvent("workspace:resized")));
  return next;
}

function createHandle(container, panels, target, sizesRef, boundary, key) {
  const axis = target.axis;
  const handle = document.createElement("button");
  handle.type = "button";
  handle.className = `workspaceSplitter workspaceSplitter--${axis}`;
  handle.dataset.boundary = String(boundary);
  handle.setAttribute("role", "separator");
  handle.setAttribute("aria-orientation", axis === "horizontal" ? "vertical" : "horizontal");
  handle.setAttribute("aria-label", axis === "horizontal"
    ? `Resize panels ${boundary + 1} and ${boundary + 2} left or right`
    : `Resize panels ${boundary + 1} and ${boundary + 2} up or down`);
  handle.title = axis === "horizontal" ? "Drag left or right to resize" : "Drag up or down to resize";
  handle.innerHTML = `<span aria-hidden="true">${axis === "horizontal" ? "↔" : "↕"}</span>`;

  let pointerStart = 0;
  let startSizes = [];

  const finish = () => {
    document.documentElement.classList.remove("workspaceIsResizing");
    handle.classList.remove("isDragging");
    saveSizes(key, sizesRef.value);
  };

  handle.addEventListener("pointerdown", (event) => {
    event.preventDefault();
    pointerStart = axis === "horizontal" ? event.clientX : event.clientY;
    startSizes = [...sizesRef.value];
    handle.setPointerCapture(event.pointerId);
    handle.classList.add("isDragging");
    document.documentElement.classList.add("workspaceIsResizing");
  });

  handle.addEventListener("pointermove", (event) => {
    if (!handle.hasPointerCapture(event.pointerId)) return;
    const pointer = axis === "horizontal" ? event.clientX : event.clientY;
    sizesRef.value = resizeBoundary(
      container,
      panels,
      axis,
      startSizes,
      boundary,
      pointer - pointerStart,
    );
  });

  handle.addEventListener("pointerup", finish);
  handle.addEventListener("pointercancel", finish);

  handle.addEventListener("keydown", (event) => {
    const negative = axis === "horizontal" ? event.key === "ArrowLeft" : event.key === "ArrowUp";
    const positive = axis === "horizontal" ? event.key === "ArrowRight" : event.key === "ArrowDown";
    if (!negative && !positive) return;
    event.preventDefault();
    const step = event.shiftKey ? 40 : 12;
    sizesRef.value = resizeBoundary(
      container,
      panels,
      axis,
      sizesRef.value,
      boundary,
      negative ? -step : step,
    );
    saveSizes(key, sizesRef.value);
  });

  handle.addEventListener("dblclick", () => {
    sizesRef.value = equalSizes(panels.length);
    applySizes(container, axis, sizesRef.value);
    positionHandles(container, axis, sizesRef.value);
    saveSizes(key, sizesRef.value);
  });

  container.appendChild(handle);
}

function enhance(container, target) {
  const panels = directPanels(container);
  if (panels.length < 2) return;

  const previousCount = Number(container.getAttribute(PANEL_COUNT) || 0);
  if (container.getAttribute(ENHANCED) === "true" && previousCount === panels.length) return;

  container.querySelectorAll(":scope > .workspaceSplitter").forEach((handle) => handle.remove());
  const currentPanels = directPanels(container);
  const key = storageKey(container, target);
  const sizesRef = { value: loadSizes(key, currentPanels.length) };
  container.setAttribute(ENHANCED, "true");
  container.setAttribute(PANEL_COUNT, String(currentPanels.length));
  container.classList.add("resizableWorkspace", `resizableWorkspace--${target.axis}`);
  currentPanels.forEach((panel) => panel.classList.add("resizableWorkspacePanel"));
  applySizes(container, target.axis, sizesRef.value);

  for (let boundary = 0; boundary < currentPanels.length - 1; boundary += 1) {
    createHandle(container, currentPanels, target, sizesRef, boundary, key);
  }
  positionHandles(container, target.axis, sizesRef.value);
}

function scan() {
  layoutTargets.forEach((target) => {
    document.querySelectorAll(target.selector).forEach((container) => enhance(container, target));
  });
}

let scanQueued = false;
function queueScan() {
  if (scanQueued) return;
  scanQueued = true;
  requestAnimationFrame(() => {
    scanQueued = false;
    scan();
  });
}

const observer = new MutationObserver(queueScan);
observer.observe(document.documentElement, { childList: true, subtree: true });
addEventListener("resize", queueScan, { passive: true });
scan();