# Resizable workspace panels

## User experience

Multi-panel workspaces can expose draggable dividers so users can give one information area more room and another less room without leaving the page.

- Vertical dividers drag left and right.
- Horizontal dividers drag up and down.
- Content reflows automatically because the underlying grid tracks are resized rather than visually scaled.
- Panel sizes are saved in local storage per route and workspace key, then restored on return.
- Double-clicking a divider restores equal panel sizes.
- Keyboard users can focus a divider and use the arrow keys; Shift plus an arrow makes a larger adjustment.
- Narrow screens fall back to a single-column layout and hide the drag controls.
- Reduced-motion preferences are respected.

## Initial coverage

The shared behavior is enabled for:

1. Command Center: change-request flow and attention panels.
2. Requirements Explorer: specifications, results, and the optional requirement inspector.

Any additional page can opt in without duplicating code:

```html
<div data-resizable-layout="horizontal" data-resizable-key="unique-workspace-name">
  <section>Left</section>
  <section>Right</section>
</div>
```

Use `data-resizable-layout="vertical"` for top/bottom sections.

## Implementation notes

The enhancement is deliberately independent of page-specific React state. A mutation observer detects supported layouts after route transitions and React re-renders. Minimum panel widths/heights prevent a section from being dragged completely out of view. A `workspace:resized` event is dispatched to each panel while resizing so future charts or canvas-based visualizations can recalculate their dimensions.
