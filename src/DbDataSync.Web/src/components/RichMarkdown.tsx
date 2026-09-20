import type { ReactNode } from 'react'
import ReactMarkdown, { type Components } from 'react-markdown'
import { Link } from 'react-router-dom'
import rehypeSlug from 'rehype-slug'
import remarkGfm from 'remark-gfm'
import { DOC_SLUGS } from './docPages'
import { isSafeImageUrl, resolveLink } from './markdownLinks'

/**
 * The full GitHub-flavoured subset — tables, task lists, strikethrough, images — for documents the build ships (phase 160).
 * `Markdown.tsx` stays the renderer for Notes: that one is deliberately almost nothing, because a note is stored input
 * rendered in somebody else's session.
 *
 * **What keeps this safe is what is not here.** `react-markdown` builds React elements and never sets HTML, and
 * `rehype-raw` — the plugin that would turn a document's own `<script>` or `<img onerror>` into real markup — is
 * deliberately not installed, so raw HTML in a source is inert text by construction, not by a sanitiser run afterwards.
 * `react-markdown` does not police URL schemes on its own, so links and images go through `markdownLinks.ts`, which
 * `Markdown.tsx` shares; `urlTransform` is the identity so that policy is the only one, not two that could disagree.
 *
 * @param docLinks resolve `other-page.md#section` to the in-app docs route and `#section` to the heading on this page.
 * Off unless the caller is showing the shipped docs, so a document that is not one of them cannot link into the app.
 */
export function RichMarkdown({ text, docLinks = false, testId }: { text: string; docLinks?: boolean; testId?: string }) {
  const slugs = docLinks ? DOC_SLUGS : undefined

  const components: Components = {
    a({ href, children }) {
      const target = resolveLink(href, slugs)
      switch (target.kind) {
        case 'external':
          return <a href={target.href} target="_blank" rel="noreferrer noopener">{children}</a>
        case 'doc':
          return <Link to={target.to}>{children}</Link>
        case 'anchor':
          return <a href={target.href}>{children}</a>
        case 'blocked':
          // The source text, so a reader can see what the document pointed at and that it was not followed.
          return <span>[{children}]({href})</span>
        default:
          return <span title={href}>{children}</span>
      }
    },
    img({ src, alt }) {
      // An image is fetched by the reader's browser the moment it renders, so it gets the strictest reading of the
      // policy: a network address only, never a relative path, and no referrer sent with the request.
      if (typeof src !== 'string' || !isSafeImageUrl(src)) return <span>{alt}</span>
      return <img src={src} alt={alt ?? ''} loading="lazy" referrerPolicy="no-referrer" />
    },
    // A table wider than the column scrolls inside itself instead of stretching the page.
    table({ children }): ReactNode {
      return <div className="rich-md-table"><table>{children}</table></div>
    },
  }

  return (
    <div className="markdown rich-markdown" data-testid={testId}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeSlug]}
        components={components}
        urlTransform={url => url}
      >
        {text}
      </ReactMarkdown>
    </div>
  )
}
