import { useEffect } from 'react'
import { Link, NavLink, useLocation, useParams } from 'react-router-dom'
import { useAbout, useDocPage } from '../api/hooks'
import { AppShell } from '../components/AppShell'
import { DOC_PAGES } from '../components/docPages'
import { ErrorBanner } from '../components/ErrorBanner'
import { RichMarkdown } from '../components/RichMarkdown'

/**
 * The docs the build shipped (phase 160): `/docs` is the index, `/docs/:page` one page of it.
 *
 * They are the files in `docs/` as they were when this version was built, so what is on screen is what is running — not
 * whatever GitHub's `main` says today. That is the point of embedding them, and why the version is shown above them.
 * Readable by every role: unlike the Admin screens, there is nothing here to withhold.
 */
export function DocsPage() {
  const { page } = useParams()
  const known = DOC_PAGES.find(p => p.slug === page)
  const { data: about } = useAbout()

  return (
    <AppShell crumbs={[{ label: 'Docs', to: page ? '/docs' : undefined }, ...(known ? [{ label: known.title }] : [])]}>
      <div className="pane">
        <div className="page-head">
          {/* The document brings its own title (its first heading), so the header stays the section's — two of the same is noise. */}
          <h1 className="page-title">Documentation</h1>
          <span className="page-note" data-testid="docs-version">
            {about?.version ? `For the version running here: ${about.version}` : 'For the version running here'}
          </span>
        </div>

        <div className="docs-layout">
          <nav className="docs-nav" aria-label="Documentation pages" data-testid="docs-nav">
            {DOC_PAGES.map(p => (
              <NavLink key={p.slug} to={`/docs/${p.slug}`} className={({ isActive }) => `docs-nav-item ${isActive ? 'active' : ''}`}>
                {p.title}
              </NavLink>
            ))}
          </nav>

          <div className="docs-body">
            {!page && <DocsIndex />}
            {page && !known && <NoSuchPage slug={page} />}
            {known && <DocBody slug={known.slug} />}
          </div>
        </div>
      </div>
    </AppShell>
  )
}

function DocsIndex() {
  return (
    <div data-testid="docs-index">
      <p className="hint" style={{ marginTop: 0 }}>
        These pages describe the version of DbDataSync you are running. They are part of the build, so they work with no
        network and cannot describe a different release.
      </p>
      <ul className="docs-index-list">
        {DOC_PAGES.map(p => (
          <li key={p.slug}><Link to={`/docs/${p.slug}`}>{p.title}</Link></li>
        ))}
      </ul>
    </div>
  )
}

function NoSuchPage({ slug }: { slug: string }) {
  return (
    <div className="banner" data-testid="docs-not-found">
      <span className="mark">!</span>
      <span>
        There is no page called <span className="mono">{slug}</span> in the docs that shipped with this version.{' '}
        <Link to="/docs">See the list of pages.</Link>
      </span>
    </div>
  )
}

function DocBody({ slug }: { slug: string }) {
  const { data, error, isLoading } = useDocPage(slug)
  const { hash } = useLocation()

  // A link to `/docs/x#section` arrives before the page's text does, and the router does not scroll to a fragment on
  // its own — so once the text is there, go to the heading it named.
  useEffect(() => {
    if (!data || !hash) return
    document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView()
  }, [data, hash])

  if (isLoading) return <span className="hint">Loading…</span>
  if (error) return <ErrorBanner error={error} />
  return <RichMarkdown text={data ?? ''} docLinks testId="docs-content" />
}
