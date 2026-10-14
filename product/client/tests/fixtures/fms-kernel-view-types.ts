import type { FmsView } from '../../src/fmsCdu/scriptedFms'
import type { DualFmsView } from '../../src/fmsCdu/dualFms'

// Compiled by the submission owner. These are the actual production interfaces received by the bench.
declare const fms: FmsView
declare const system: DualFmsView
// @ts-expect-error A nested wind write must use ios.wind through submit.
fms.wind.speed = 45
// @ts-expect-error A nested active route mutation must use a CDU or IOS action.
fms.activeRoute.legs.splice(0, 1)
// @ts-expect-error The peer computer cannot expose an aircraft write through its view.
system.computers[1].aircraft.heading = 90
// @ts-expect-error A communication port cannot expose tuning mutation to the observer.
fms.radioPort?.tune('com1', '123.45')
// @ts-expect-error The peer port cannot expose mode mutation to the observer.
fms.dualOperation?.setIndependent(true)
// @ts-expect-error Observers cannot edit an authoritative engineering timestamp.
fms.engineeringLog[0].at.setTime(0)
// @ts-expect-error The actual readonly-set path cannot reselect navigation equipment.
fms.deselectedInputs.add('TAS')
// @ts-expect-error Query results must also close mutable navigation-array escape paths.
fms.navdb.find('TEST').splice(0, 1)

// Positive controls: the view still supports real display reads and subscriptions.
fms.screen()
fms.wind.speed.toFixed(1)
fms.activeRoute.legs.map(leg => leg.kind)
system.simulator.guidance.mode
