import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Checkpoint-1 diagnostics only. No production source is modified; this dev-only transform
// adds observations without changing placement, selection, input, or framing decisions.
export default defineConfig({ plugins: [{
  name: '1046-observe', enforce: 'pre',
  transform(source, id) {
    if (!id.endsWith('/src/DigitalThreadCanvas.tsx')) return
    const log = (kind: string, data: string) => `;(window as any).__1046?.push({kind:${JSON.stringify(kind)},t:performance.now(),${data}});`
    source = source.replace('lastPaintedPositions.current = positions', 'lastPaintedPositions.current = positions' + log('paint',
      'selectedId,emphasisId,box,transform:{...transform.current},display:{...display},tier:result.tier,heights:[...measuredCardHeights],positions:[...positions],targets:[...revealTargets.current],deltas:[...revealDeltas.current],offsets:[...offsets.current],frozen:[...frozenLanes.current],visited:[...visitedLanes.current],cameraOwned:cameraOwned.current,framedFor:framedFor.current'))
    source = source.replace('const retained = new Map(revealTargets.current)', log('plan', 'selectedId,emphasisId,retainedSubjectY:retainedSubjectY.current,constraintsChanged,windows:[...contentWindows],existing:[...revealTargets.current]') + 'const retained = new Map(revealTargets.current)')
    source = source.replace('if (!target) return false', log('frame', 'target,explicit,cameraOwned:cameraOwned.current') + 'if (!target) return false')
    source = source.replace('const onSelect = useCallback((id: string | null) => {', 'const onSelect = useCallback((id: string | null) => {' + log('select', 'id'))
    source = source.replace('activeGesture.current?.()\n    frozenLanes.current', log('scope-reset', 'scopeKey,selectedId,emphasisId,framedFor:framedFor.current') + 'activeGesture.current?.()\n    frozenLanes.current')
    return source
  },
}, react()] })
