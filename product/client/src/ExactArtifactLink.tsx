import type { MouseEvent, ReactNode } from 'react'

/**
 * The small shared link primitive for controlled identifiers.
 *
 * A route is deliberately optional: an exact projection node may be useful context while still not being
 * addressable by the current workspace. In that case we render an explicit, non-clickable value rather than
 * a misleading `#` link or a route that silently opens a different revision.
 */
export default function ExactArtifactLink({
  href,
  children,
  className,
  onOpen,
  title,
}: {
  href?: string
  children: ReactNode
  className?: string
  onOpen?: () => void
  title?: string
}) {
  const classes = ['exactArtifactLink', className].filter(Boolean).join(' ')
  // A hash placeholder is not an exact route. Treat legacy callers that still
  // pass it as unresolved rather than rendering a misleading self-link.
  const exactHref = href && href !== '#' ? href : undefined
  if (!exactHref) {
    return <span className={`${classes} unresolved`} data-exact-artifact-link="unresolved" title={title ?? 'This exact artifact is not openable in the current scope'}>{children}</span>
  }

  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    // Keep modified and non-primary clicks native so Ctrl/Cmd-click, middle-click, copy-link, and open in a
    // new tab all retain normal browser semantics. Callers may use the optional callback for SPA navigation
    // on an ordinary primary click without taking ownership of the link itself.
    if (onOpen && event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
      event.preventDefault()
      onOpen()
    }
  }

  return <a className={classes} href={exactHref} onClick={handleClick} title={title}>{children}</a>
}
