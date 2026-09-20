import { describe, expect, it } from 'vitest'
import { DOC_PAGES, DOC_SLUGS } from './docPages'
import { resolveLink } from './markdownLinks'

// The repo's docs/, read at test time — the same files the build copies into the package.
const sources = import.meta.glob('../../../../docs/*.md', { query: '?raw', import: 'default', eager: true }) as Record<string, string>
const docs = Object.entries(sources).map(([path, text]) => ({ slug: path.split('/').pop()!.replace(/\.md$/, ''), text }))

describe('DOC_PAGES', () => {
  it('lists exactly the pages in docs/', () => {
    expect(docs.map(d => d.slug).sort()).toEqual([...DOC_SLUGS].sort())
  })

  it('has a title for every page and no page twice', () => {
    expect(new Set(DOC_SLUGS).size).toBe(DOC_PAGES.length)
    for (const page of DOC_PAGES) expect(page.title.trim()).not.toBe('')
  })
})

describe('the links between the docs', () => {
  // Every relative link a page makes to a `.md` must resolve to a page that ships — otherwise the viewer would show it
  // as plain text, and this is where a renamed or removed page gets caught rather than by a reader.
  it.each(docs.map(d => [d.slug, d.text] as const))('every page link in %s.md resolves', (_slug, text) => {
    const links = [...text.matchAll(/\]\(([^)\s]+)\)/g)].map(m => m[1])
      .filter(href => /\.md(#|$)/.test(href) && !/^[a-z][a-z0-9+.-]*:/i.test(href))

    for (const href of links)
      expect(resolveLink(href, DOC_SLUGS).kind, `${href}`).toBe('doc')
  })
})
