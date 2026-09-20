import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { RichMarkdown } from './RichMarkdown'

function render(text: string, docLinks = false): string {
  return renderToStaticMarkup(<MemoryRouter><RichMarkdown text={text} docLinks={docLinks} /></MemoryRouter>)
}

describe('RichMarkdown', () => {
  it('renders a GitHub table as a table — the thing Markdown.tsx cannot', () => {
    const html = render('| a | b |\n| - | - |\n| 1 | 2 |\n')

    expect(html).toContain('<table>')
    expect(html).toContain('<th>a</th>')
    expect(html).toContain('<td>2</td>')
    expect(html).toContain('rich-md-table')
  })

  it('renders task lists and strikethrough', () => {
    const html = render('- [x] done\n- [ ] todo\n\n~~gone~~')

    expect(html).toContain('type="checkbox"')
    expect(html).toContain('<del>gone</del>')
  })

  it('gives headings the ids GitHub gives them, so a docs anchor lands', () => {
    const html = render('## Descriptor drivers\n\n## What `serve` does?')

    expect(html).toContain('<h2 id="descriptor-drivers">')
    expect(html).toContain('id="what-serve-does"')
  })

  describe('a source that tries to act', () => {
    // Raw HTML is text here, never markup: rehype-raw is deliberately absent. Escaped, it appears with `&lt;`; a live
    // tag would appear with a bare `<`, which is what every case below rules out.
    it.each([
      '<script>alert(1)</script>',
      '<img src=x onerror=alert(1)>',
      '<a href="javascript:alert(1)">x</a>',
      '<iframe src="https://evil.example"></iframe>',
      '<svg onload=alert(1)>',
      '<div onclick="alert(1)">x</div>',
      '<style>body{display:none}</style>',
    ])('leaves %s as inert text', source => {
      const html = render(`before\n\n${source}\n\nafter`)

      expect(html).not.toMatch(/<(script|img|iframe|svg|style)\b/i)
      expect(html).not.toMatch(/<div onclick/i)
      expect(html).not.toMatch(/<a [^>]*javascript:/i)
    })

    it.each([
      '[x](javascript:alert(1))',
      '[x](JaVaScRiPt:alert(1))',
      '[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)',
      '[x](vbscript:msgbox(1))',
      '[x](//evil.example.com)',
    ])('does not make %s a link', source => {
      const html = render(source)

      expect(html).not.toMatch(/<a\b/i)
      expect(html).not.toMatch(/href=/i)
      expect(html).toContain('[x](')
    })

    it('does not fetch an image from a data:, javascript: or relative address', () => {
      for (const src of ['data:image/svg+xml,<svg onload=alert(1)>', 'javascript:alert(1)', 'images/x.png', '//evil.example.com/x.png'])
        expect(render(`![alt text](${src})`), src).not.toMatch(/<img\b/i)
    })

    it('turns an unsafe image into its alt text', () => {
      expect(render('![the alt](javascript:alert(1))')).toContain('the alt')
    })
  })

  describe('links a reader can follow', () => {
    it('opens an external link in a new tab without leaking the opener', () => {
      const html = render('[site](https://example.com)')

      expect(html).toContain('href="https://example.com"')
      expect(html).toContain('target="_blank"')
      expect(html).toContain('rel="noreferrer noopener"')
    })

    it('loads a remote image lazily, and without sending a referrer', () => {
      const html = render('![shot](https://raw.githubusercontent.com/x/y.png)')

      expect(html).toContain('src="https://raw.githubusercontent.com/x/y.png"')
      expect(html).toContain('loading="lazy"')
      expect(html).toContain('referrerPolicy="no-referrer"')
    })

    it('sends another page of the docs to its app route, anchor and all — when it is showing the docs', () => {
      const html = render('[drivers](drivers-and-libraries.md#descriptor-drivers)', true)

      expect(html).toContain('href="/docs/drivers-and-libraries#descriptor-drivers"')
      expect(html).not.toContain('target="_blank"')
    })

    it('keeps an in-page anchor in the page', () => {
      expect(render('[up](#top-of-page)', true)).toContain('href="#top-of-page"')
    })

    it('shows a link to a page that did not ship as its label, not as a dead link', () => {
      const html = render('see [the notes](nothing-like-this.md)', true)

      expect(html).not.toMatch(/<a\b/i)
      expect(html).toContain('the notes')
    })

    it('does not resolve a relative page link at all when it is not showing the docs', () => {
      const html = render('[install](install.md)')

      expect(html).not.toMatch(/<a\b/i)
      expect(html).toContain('install')
    })
  })
})
