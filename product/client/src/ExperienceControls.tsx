export type WorkspaceDensity = 'comfortable' | 'compact'
export type MotionPreference = 'full' | 'reduced'

type Props = {
  open: boolean
  density: WorkspaceDensity
  motion: MotionPreference
  onDensityChange: (density: WorkspaceDensity) => void
  onMotionChange: (motion: MotionPreference) => void
  onClose: () => void
}

export default function ExperienceControls({open,density,motion,onDensityChange,onMotionChange,onClose}:Props){
  if(!open)return null
  return <div className="experienceBackdrop" role="presentation" onMouseDown={event=>{if(event.target===event.currentTarget)onClose()}}>
    <aside className="experienceDrawer" role="dialog" aria-modal="true" aria-label="Workspace display">
      <header>
        <div><span>WORKSPACE DISPLAY</span><h2>Make the view yours</h2><p>Choose how much information you see and how the interface responds.</p></div>
        <button className="experienceClose" onClick={onClose} aria-label="Close workspace display">×</button>
      </header>
      <section>
        <div className="experienceSectionTitle"><div><h3>Information density</h3><p>Comfortable is optimized for presenting. Compact keeps more engineering records in view.</p></div><b>{density}</b></div>
        <div className="experienceChoice" role="group" aria-label="Information density">
          <button aria-pressed={density==='comfortable'} onClick={()=>onDensityChange('comfortable')}><i className="densityPreview comfortable"><span/><span/><span/></i><b>Comfortable</b><small>More breathing room</small></button>
          <button aria-pressed={density==='compact'} onClick={()=>onDensityChange('compact')}><i className="densityPreview compact"><span/><span/><span/><span/></i><b>Compact</b><small>More records in view</small></button>
        </div>
      </section>
      <section>
        <div className="experienceSectionTitle"><div><h3>Interface motion</h3><p>Motion reinforces navigation and state changes. Reduced mode removes nonessential transitions.</p></div><b>{motion}</b></div>
        <div className="motionChoice" role="group" aria-label="Interface motion">
          <button aria-pressed={motion==='full'} onClick={()=>onMotionChange('full')}><span className="motionGlyph" aria-hidden="true">→</span><div><b>Purposeful motion</b><small>Show spatial and state transitions</small></div></button>
          <button aria-pressed={motion==='reduced'} onClick={()=>onMotionChange('reduced')}><span className="motionGlyph quiet" aria-hidden="true">—</span><div><b>Reduced motion</b><small>Keep every transition immediate</small></div></button>
        </div>
      </section>
      <footer><span aria-hidden="true">✓</span><div><b>Saved on this device</b><small>Your choice follows you across AeroLink workspaces in this browser.</small></div></footer>
    </aside>
  </div>
}
