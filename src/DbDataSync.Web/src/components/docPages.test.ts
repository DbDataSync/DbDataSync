import { describe, expect, it } from 'vitest'
import { DOC_PAGES, DOC_SLUGS } from './docPages'
import { resolveDocImage, resolveLink } from './markdownLinks'

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

// Every picture the docs show must exist, or a reader gets a broken image and nobody finds out until they do.
const screenshots = Object.keys(import.meta.glob('../../../../screenshots/**/*.png'))
  .map(path => path.replace(/^.*\/screenshots\//, ''))

describe('the pictures in the docs', () => {
  const referenced = docs.flatMap(d => [...d.text.matchAll(/!\[[^\]]*\]\(([^)\s]+)\)/g)].map(m => ({ slug: d.slug, src: m[1] })))

  it('are written relative to the docs folder, never as an address that goes stale', () => {
    for (const { slug, src } of referenced)
      expect(src, `${slug}.md`).not.toMatch(/^[a-z][a-z0-9+.-]*:|^\/\//i)
  })

  it('all resolve to a file in screenshots/', () => {
    expect(referenced.length).toBeGreaterThan(0)
    for (const { slug, src } of referenced) {
      const served = resolveDocImage(src)
      expect(served, `${slug}.md: ${src}`).not.toBeNull()
      expect(screenshots, `${slug}.md: ${src}`).toContain(served!.replace(/^\/screenshots\//, ''))
    }
  })
})

// docs/images.txt is what the build ships (csproj, Dockerfile, and the release checks all read it). It has to be exactly
// the pictures the docs show: a missing line is a broken image in the embedded docs, an extra one is dead weight in every package.
const manifests = import.meta.glob('../../../../docs/images.txt', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

describe('docs/images.txt', () => {
  const listed = Object.values(manifests)[0]?.split(/\r?\n/).filter(line => line.trim()) ?? []
  const shown = [...new Set(docs.flatMap(d => [...d.text.matchAll(/!\[[^\]]*\]\(\.\.\/(screenshots\/[^)\s]+)\)/g)].map(m => m[1])))]

  it('exists', () => {
    expect(Object.keys(manifests)).toHaveLength(1)
  })

  it('lists exactly the pictures the docs show', () => {
    expect([...listed].sort()).toEqual([...shown].sort())
  })

  it('lists files that exist', () => {
    for (const image of listed) expect(screenshots, image).toContain(image.replace(/^screenshots\//, ''))
  })
})
