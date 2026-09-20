#!/usr/bin/env node
// Phase 162 — the README that goes into the NuGet package.
//
// The README in the repo is written with relative links (`docs/install.md`), which is what GitHub, an offline checkout and the
// embedded docs all want. nuget.org renders the README on the package page, away from the repository, where a relative path
// goes nowhere. So the package gets a copy with every relative link and image made absolute, pinned to the commit that was
// packed — not `main` (a moving target: the listing would drift from the release) and not the release tag (it does not
// exist yet: release.yml tags after the package is confirmed published).
//
//   node tools/docs/nuget-readme.mjs --in README.md --out obj/nuget-readme/README.md [--ref <commit>] [--repo owner/name]
//
// Fails, listing every problem, when a relative link points at a file that is not there — a broken link fails the pack
// instead of shipping.

import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const SCHEME = /^[a-z][a-z0-9+.-]*:/i

/**
 * @param {string} markdown
 * @param {{ repo: string, ref: string, baseDir: string, exists?: (p: string) => boolean }} options
 *   `baseDir` is the directory the links are relative to (the README's own); `exists` is injectable for tests.
 * @returns {{ text: string, problems: string[] }}
 */
export function rewriteReadme(markdown, { repo, ref, baseDir, exists = fs.existsSync }) {
  const problems = []
  const root = path.resolve(baseDir)

  const absolute = (target, isImage, where) => {
    // Only what is relative: anything with a scheme (https:, mailto:), a protocol-relative URL, or an in-page anchor is
    // already something nuget.org can follow, or nothing to fix.
    if (!target || SCHEME.test(target) || target.startsWith('//') || target.startsWith('#')) return target

    const [pathPart, ...rest] = target.split(/(?=[#?])/)
    const tail = rest.join('')
    const resolved = path.resolve(root, decodeURI(pathPart))
    const relative = path.relative(root, resolved).split(path.sep).join('/')

    if (relative.startsWith('..') || path.isAbsolute(relative)) {
      problems.push(`${where}: '${target}' points outside the repository`)
      return target
    }
    if (!exists(resolved)) {
      problems.push(`${where}: '${target}' does not exist`)
      return target
    }

    const kind = isImage ? null : fs.existsSync(resolved) && fs.statSync(resolved).isDirectory() ? 'tree' : 'blob'
    return isImage
      ? `https://raw.githubusercontent.com/${repo}/${ref}/${relative}${tail}`
      : `https://github.com/${repo}/${kind}/${ref}/${relative}${tail}`
  }

  let inFence = false
  const lines = markdown.split('\n').map((line, index) => {
    if (/^\s*(```|~~~)/.test(line)) {
      inFence = !inFence
      return line
    }
    if (inFence) return line

    const where = `README line ${index + 1}`
    // Code spans are left alone: `[x](y)` inside backticks is text, not a link.
    return line.split(/(`+[^`]*`+)/).map((part) => {
      if (part.startsWith('`')) return part
      return part
        .replace(/(!?)\[([^\]]*)\]\(\s*(<[^>]*>|[^)\s]*)((?:\s+"[^"]*")?)\s*\)/g,
          (_, bang, text, target, title) => {
            const bare = target.startsWith('<') ? target.slice(1, -1) : target
            return `${bang}[${text}](${absolute(bare, bang === '!', where)}${title})`
          })
        // Reference-style definitions: [id]: target
        .replace(/^(\s{0,3}\[[^\]]+\]:\s*)(\S+)/, (_, head, target) => `${head}${absolute(target, false, where)}`)
    }).join('')
  })

  return { text: lines.join('\n'), problems }
}

function argument(args, name) {
  const index = args.indexOf(name)
  return index >= 0 ? args[index + 1] : undefined
}

function currentCommit(cwd) {
  try {
    return execFileSync('git', ['rev-parse', 'HEAD'], { cwd, stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim()
  } catch {
    return undefined
  }
}

function main(args) {
  const input = argument(args, '--in')
  const output = argument(args, '--out')
  if (!input || !output) {
    console.error('usage: nuget-readme.mjs --in <README.md> --out <file> [--ref <commit>] [--repo <owner/name>]')
    return 2
  }

  const baseDir = path.dirname(path.resolve(input))
  const ref = argument(args, '--ref') || currentCommit(baseDir) || 'main'
  const repo = argument(args, '--repo') || 'DbDataSync/DbDataSync'

  const { text, problems } = rewriteReadme(fs.readFileSync(input, 'utf8'), { repo, ref, baseDir })
  if (problems.length > 0) {
    console.error(`${input} has links that cannot be made absolute:\n${problems.map((p) => `  ${p}`).join('\n')}`)
    return 1
  }

  fs.mkdirSync(path.dirname(path.resolve(output)), { recursive: true })
  fs.writeFileSync(output, text)
  console.log(`nuget-readme: wrote ${output}, links pinned to ${ref}`)
  return 0
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exit(main(process.argv.slice(2)))
