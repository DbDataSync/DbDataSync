/**
 * What a link in rendered Markdown is allowed to be — one policy, shared by every renderer, so there is exactly one
 * place that decides which destinations a stored document can point a reader at.
 *
 * An **allowlist**, not a blocklist: the only things that ever become an `href` are a URL with a scheme this file
 * names, an in-document `#anchor`, or a relative link to a page that ships with the app. Everything else — every other
 * scheme, `//host` (which a browser resolves against the page's own scheme), a link with whitespace or control
 * characters in front of it, a relative path that is not one of our pages — becomes text. A form nobody thought of
 * therefore fails safe: it is not in the list.
 */
export function isSafeExternalUrl(href: string): boolean {
  return /^(https?:|mailto:)/i.test(href)
}

/** Images are fetched the instant they render, so only a network address qualifies — `mailto:` is a link, not a picture. */
export function isSafeImageUrl(src: string): boolean {
  return /^https?:/i.test(src)
}

/** A picture in the shipped docs is written `../screenshots/<folder>/<file>.png` — a path that works on GitHub — and the build
 *  puts the same files at `/screenshots/<folder>/<file>`, so the docs are read without rewriting a word of them. Raster
 *  formats only, one folder deep, and no path separators in the name: nothing here can point outside that folder. */
const DOC_IMAGE = /^\.\.\/screenshots\/([a-z0-9-]+)\/([A-Za-z0-9._-]+\.(?:png|jpe?g|gif|webp))$/i

/** Where the app serves a docs page's own picture, or null for anything that is not one. */
export function resolveDocImage(src: string): string | null {
  const match = DOC_IMAGE.exec(src)
  return match ? `/screenshots/${match[1]}/${match[2]}` : null
}

export type LinkTarget =
  /** An address outside the app: opens in a new tab. */
  | { kind: 'external'; href: string }
  /** A page of the embedded docs, as an app route (`/docs/install#section`). */
  | { kind: 'doc'; to: string }
  /** A heading in the page being read. */
  | { kind: 'anchor'; href: string }
  /** A relative link that goes nowhere we can take a reader — shown as its label. */
  | { kind: 'unavailable' }
  /** A scheme that is not on the allowlist — shown as the source text it was written as. */
  | { kind: 'blocked' }

/** Every page opens with a nav line whose first link is `[DbDataSync](../README.md)` — the repository's front page.
 *  In the app the same place is the docs index. */
const README_LINK = /^\.\.\/README\.md$/i

const DOC_LINK = /^(?:\.\/)?([a-z0-9-]+)\.md(#[A-Za-z0-9_-]*)?$/i

/**
 * @param docSlugs the pages that ship. Present only for a renderer that is showing the docs: a note, which is not one
 * of the app's own pages, gets no relative resolution at all, so `[install](install.md)` in a note is not a link.
 */
export function resolveLink(href: string | undefined, docSlugs?: readonly string[]): LinkTarget {
  if (!href) return { kind: 'unavailable' }
  if (isSafeExternalUrl(href)) return { kind: 'external', href }
  // Any other scheme, or a protocol-relative URL, is a destination the reader did not agree to be sent to.
  if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('//')) return { kind: 'blocked' }

  if (docSlugs) {
    if (/^#[A-Za-z0-9_-]+$/.test(href)) return { kind: 'anchor', href }
    if (README_LINK.test(href)) return { kind: 'doc', to: '/docs' }
    const match = DOC_LINK.exec(href)
    const slug = match?.[1].toLowerCase()
    if (match && slug && docSlugs.includes(slug)) return { kind: 'doc', to: `/docs/${slug}${match[2] ?? ''}` }
  }

  return { kind: 'unavailable' }
}
