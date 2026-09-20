import { describe, expect, it } from 'vitest'
import { isSafeExternalUrl, isSafeImageUrl, resolveLink } from './markdownLinks'

const SLUGS = ['install', 'configuration', 'drivers-and-libraries']

describe('resolveLink', () => {
  it('lets http, https and mailto out of the app, and nothing else', () => {
    expect(resolveLink('https://example.com/a?b=c#d')).toEqual({ kind: 'external', href: 'https://example.com/a?b=c#d' })
    expect(resolveLink('HTTP://example.com')).toEqual({ kind: 'external', href: 'HTTP://example.com' })
    expect(resolveLink('mailto:ops@example.com')).toEqual({ kind: 'external', href: 'mailto:ops@example.com' })
  })

  it.each([
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',
    'file:///etc/passwd',
    'ftp://example.com/x',
    '//evil.example.com/path',
  ])('blocks %s', href => {
    expect(resolveLink(href, SLUGS)).toEqual({ kind: 'blocked' })
    expect(resolveLink(href)).toEqual({ kind: 'blocked' })
  })

  // Browsers strip tabs and newlines from inside a URL's scheme, and ignore leading whitespace and control characters,
  // so these would run if they ever reached an href. They must not match anything on the allowlist.
  it.each([' javascript:alert(1)', 'java\tscript:alert(1)', 'java\nscript:alert(1)', '\u0001javascript:alert(1)', ' https://example.com'])(
    'never emits a link for the disguised %j', href => {
      const target = resolveLink(href, SLUGS)
      expect(['external', 'doc', 'anchor']).not.toContain(target.kind)
    })

  it('maps another page of the docs to its app route, keeping the anchor', () => {
    expect(resolveLink('install.md', SLUGS)).toEqual({ kind: 'doc', to: '/docs/install' })
    expect(resolveLink('./install.md', SLUGS)).toEqual({ kind: 'doc', to: '/docs/install' })
    expect(resolveLink('drivers-and-libraries.md#descriptor-drivers', SLUGS))
      .toEqual({ kind: 'doc', to: '/docs/drivers-and-libraries#descriptor-drivers' })
  })

  it("sends the README link every page opens with to the docs index, the app's front page for them", () => {
    expect(resolveLink('../README.md', SLUGS)).toEqual({ kind: 'doc', to: '/docs' })
  })

  it('keeps an in-page anchor as one', () => {
    expect(resolveLink('#reader-kinds', SLUGS)).toEqual({ kind: 'anchor', href: '#reader-kinds' })
  })

  it('does not take a reader to a page that did not ship, or out of the docs folder', () => {
    expect(resolveLink('not-a-page.md', SLUGS)).toEqual({ kind: 'unavailable' })
    expect(resolveLink('../CHANGELOG.md', SLUGS)).toEqual({ kind: 'unavailable' })
    expect(resolveLink('../screenshots/x.png', SLUGS)).toEqual({ kind: 'unavailable' })
    expect(resolveLink('/etc/passwd', SLUGS)).toEqual({ kind: 'unavailable' })
    expect(resolveLink('', SLUGS)).toEqual({ kind: 'unavailable' })
    expect(resolveLink(undefined, SLUGS)).toEqual({ kind: 'unavailable' })
  })

  it('resolves no relative link at all unless it is showing the docs', () => {
    expect(resolveLink('install.md')).toEqual({ kind: 'unavailable' })
    expect(resolveLink('#anchor')).toEqual({ kind: 'unavailable' })
  })
})

describe('the shared predicates', () => {
  it('agree with resolveLink about what leaves the app', () => {
    expect(isSafeExternalUrl('https://example.com')).toBe(true)
    expect(isSafeExternalUrl('mailto:a@b.c')).toBe(true)
    expect(isSafeExternalUrl('javascript:alert(1)')).toBe(false)
    expect(isSafeExternalUrl('install.md')).toBe(false)
  })

  it('lets an image come from the network only', () => {
    expect(isSafeImageUrl('https://raw.githubusercontent.com/x.png')).toBe(true)
    expect(isSafeImageUrl('mailto:a@b.c')).toBe(false)
    expect(isSafeImageUrl('data:image/svg+xml,<svg onload=alert(1)>')).toBe(false)
    expect(isSafeImageUrl('images/x.png')).toBe(false)
  })
})
