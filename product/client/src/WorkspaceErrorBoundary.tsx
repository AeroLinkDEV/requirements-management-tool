import { Component } from 'react'
import type { ErrorInfo, ReactNode } from 'react'
import './WorkspaceErrorBoundary.css'

type Props = { children: ReactNode }
type State = { message: string }

/**
 * Keeps a rendering failure inside the workspace instead of unmounting the application.
 *
 * Without this, one unguarded read of a failed response blanks the entire page, and the only recovery is a
 * manual reload with no explanation. A controlled tool must fail visibly and recoverably: the user is told
 * what happened, the navigation context is stated, and reloading is offered as an explicit action.
 */
export default class WorkspaceErrorBoundary extends Component<Props, State> {
  state: State = { message: '' }

  static getDerivedStateFromError(error: unknown): State {
    return { message: error instanceof Error ? error.message : 'An unexpected workspace error occurred.' }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Left on the console deliberately: local operators and browser journeys rely on it for diagnosis.
    console.error('AeroLink workspace error', error, info.componentStack)
  }

  render() {
    if (!this.state.message) return this.props.children
    return (
      <div className="workspaceCrash" role="alert">
        <div>
          <p className="eyebrow">WORKSPACE ERROR</p>
          <h1>This screen could not be displayed</h1>
          <p>
            No controlled data was changed. Reload to return to a working state; if it happens again, note
            what you were doing and report it with the detail below.
          </p>
          <code>{this.state.message}</code>
          <div>
            <button onClick={() => window.location.reload()}>Reload AeroLink</button>
            <button className="outline" onClick={() => this.setState({ message: '' })}>Try this screen again</button>
          </div>
        </div>
      </div>
    )
  }
}
