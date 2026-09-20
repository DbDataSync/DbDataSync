/**
 * The pages of `docs/` that ship with the app (phase 160), in the order the index lists them — the order the README
 * lists them in. The slug is the file's stem: `install` is `docs/install.md`, served at `/docs/install.md` and
 * shown at the route `/docs/install`. `docPages.test.ts` fails if this list and the folder ever disagree, so adding
 * a page to `docs/` without adding it here (or the reverse) does not reach a build.
 */
export const DOC_PAGES = [
  { slug: 'install', title: 'Install' },
  { slug: 'configuration', title: 'Configuration' },
  { slug: 'getting-started', title: 'Getting started' },
  { slug: 'replication-concepts', title: 'Replication concepts' },
  { slug: 'drivers-and-libraries', title: 'Drivers and libraries' },
  { slug: 'state-database', title: 'State database' },
  { slug: 'development', title: 'Building from source' },
] as const

export const DOC_SLUGS: readonly string[] = DOC_PAGES.map(page => page.slug)
