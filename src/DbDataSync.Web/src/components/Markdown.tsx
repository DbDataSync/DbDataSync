import type { ReactNode } from 'react'
import { isSafeExternalUrl } from './markdownLinks'

/**
 * Renders Markdown as React elements.
 *
 * **Deliberately not a library, and deliberately not `dangerouslySetInnerHTML`.** Notes are typed by
 * an operator and read by everybody else who opens the replication, which makes them stored input
 * rendered in someone else's session — the exact shape a stored-XSS is. Producing elements rather than
 * an HTML string means there is no injection surface to sanitise, because there is no HTML being
 * parsed: text becomes text nodes, and the only tags that ever exist are the ones written here.
 *
 * The subset is the subset a note is written in — headings, emphasis, code, links, lists, quotes,
 * rules. Anything outside it renders as its own literal text rather than disappearing, which is the
 * right failure for a field whose purpose is that somebody reads what was written.
 */
export function Markdown({ text, testId }: { text: string; testId?: string }) {
  return <div className="markdown" data-testid={testId}>{blocks(text)}</div>
}

function blocks(text: string): ReactNode[] {
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  const out: ReactNode[] = []
  let i = 0

  while (i < lines.length) {
    const line = lines[i]

    if (!line.trim()) { i++; continue }

    // Fenced code. Unterminated fences run to the end of the document rather than falling back to
    // paragraphs — somebody who opened a fence meant the rest to be code.
    const fence = /^\s*```(\w+)?\s*$/.exec(line)
    if (fence) {
      const body: string[] = []
      i++
      while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) body.push(lines[i++])
      i++
      out.push(<pre key={out.length} className="md-pre"><code>{body.join('\n')}</code></pre>)
      continue
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line)
    if (heading) {
      const level = heading[1].length
      const Tag = `h${Math.min(level + 2, 6)}` as 'h3' | 'h4' | 'h5' | 'h6'
      // Shifted down two levels: these live inside a card on a page that already has an h1 and an h2,
      // and a note's own "# Owner" is a heading within that, not a competing page title.
      out.push(<Tag key={out.length} className={`md-h md-h${level}`}>{inline(heading[2])}</Tag>)
      i++
      continue
    }

    if (/^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) {
      out.push(<hr key={out.length} className="md-hr" />)
      i++
      continue
    }

    if (/^\s*>/.test(line)) {
      const body: string[] = []
      while (i < lines.length && /^\s*>/.test(lines[i])) body.push(lines[i++].replace(/^\s*>\s?/, ''))
      out.push(<blockquote key={out.length} className="md-quote">{blocks(body.join('\n'))}</blockquote>)
      continue
    }

    const bullet = /^\s*([-*+])\s+/
    const numbered = /^\s*\d+[.)]\s+/
    if (bullet.test(line) || numbered.test(line)) {
      const ordered = !bullet.test(line)
      const marker = ordered ? numbered : bullet
      const items: string[] = []
      while (i < lines.length && marker.test(lines[i])) items.push(lines[i++].replace(marker, ''))
      const children = items.map((item, n) => <li key={n}>{inline(item)}</li>)
      out.push(ordered
        ? <ol key={out.length} className="md-list">{children}</ol>
        : <ul key={out.length} className="md-list">{children}</ul>)
      continue
    }

    // A paragraph runs until a blank line or the start of any other block.
    const paragraph: string[] = []
    while (i < lines.length && lines[i].trim()
      && !/^\s*(```|>|#{1,6}\s|-{3,}\s*$)/.test(lines[i])
      && !bullet.test(lines[i]) && !numbered.test(lines[i])) paragraph.push(lines[i++])
    out.push(<p key={out.length} className="md-p">{inline(paragraph.join('\n'))}</p>)
  }

  return out
}

/**
 * Inline spans, innermost-first: code wins over everything, so a backticked `**x**` stays literal.
 *
 * One regex with alternatives rather than successive passes over the string. Successive passes would
 * have to re-scan text that earlier passes already turned into elements, and that is exactly how a
 * renderer ends up formatting the inside of a code span.
 */
const INLINE = /(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*]+\*)|(_[^_]+_)|(\[[^\]]*]\([^)\s]+\))/

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = []
  let rest = text
  let key = 0

  while (rest) {
    const match = INLINE.exec(rest)
    if (!match) { out.push(rest); break }

    if (match.index > 0) out.push(rest.slice(0, match.index))
    const token = match[0]

    if (token.startsWith('`')) out.push(<code key={key++} className="md-code">{token.slice(1, -1)}</code>)
    else if (token.startsWith('**')) out.push(<strong key={key++}>{token.slice(2, -2)}</strong>)
    else if (token.startsWith('*')) out.push(<em key={key++}>{token.slice(1, -1)}</em>)
    else if (token.startsWith('_')) out.push(<em key={key++}>{token.slice(1, -1)}</em>)
    else {
      const [, label, href] = /^\[([^\]]*)]\(([^)\s]+)\)$/.exec(token)!
      // Anything but http, https and mailto renders as the text it was written as. `javascript:` in an
      // href is the one thing in a note that could act rather than inform, and a note is written by
      // one operator and opened by another.
      out.push(isSafeExternalUrl(href)
        ? <a key={key++} href={href} target="_blank" rel="noreferrer noopener">{label || href}</a>
        : <span key={key++}>{token}</span>)
    }

    rest = rest.slice(match.index + token.length)
  }

  return out
}
