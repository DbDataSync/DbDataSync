import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { test } from 'node:test'
import { execFileSync, spawnSync } from 'node:child_process'
import { rewriteReadme } from './nuget-readme.mjs'

const repo = 'DbDataSync/DbDataSync'
const ref = '0123456789abcdef0123456789abcdef01234567'
const here = path.dirname(new URL(import.meta.url).pathname)
const realRoot = path.resolve(here, '../..')

// A pretend repository: only these files exist, so the tests never depend on the real docs.
const files = new Set(['docs/install.md', 'docs/configuration.md', 'screenshots/golden-path/a.png'])
const exists = (p) => files.has(path.relative('/repo', p).split(path.sep).join('/')) || p === path.resolve('/repo/docs')
const rewrite = (text) => rewriteReadme(text, { repo, ref, baseDir: '/repo', exists })

test('a relative link becomes a blob link pinned to the commit', () => {
  const { text, problems } = rewrite('[Install](docs/install.md)')

  assert.deepEqual(problems, [])
  assert.equal(text, `[Install](https://github.com/${repo}/blob/${ref}/docs/install.md)`)
})

test('a fragment survives, and an image goes to raw.githubusercontent.com', () => {
  const { text } = rewrite('[c](docs/configuration.md#the-key) ![shot](screenshots/golden-path/a.png)')

  assert.equal(text,
    `[c](https://github.com/${repo}/blob/${ref}/docs/configuration.md#the-key) ` +
    `![shot](https://raw.githubusercontent.com/${repo}/${ref}/screenshots/golden-path/a.png)`)
})

test('a leading ./ and a ../ that stays inside the repository are normalised', () => {
  const { text, problems } = rewriteReadme('[a](./docs/install.md) [b](docs/../docs/install.md)',
    { repo, ref, baseDir: '/repo', exists })

  assert.deepEqual(problems, [])
  assert.equal(text.match(new RegExp(`blob/${ref}/docs/install.md`, 'g')).length, 2)
})

test('anything already absolute, a mailto:, and an in-page anchor are left as they are', () => {
  const input = '[n](https://www.nuget.org/packages/DbDataSync) [m](mailto:a@b.c) [a](#top) [p](//cdn.example.com/x) ' +
    '[![NuGet](https://img.shields.io/nuget/v/DbDataSync.svg)](https://www.nuget.org/packages/DbDataSync)'

  assert.equal(rewrite(input).text, input)
})

test('a link to a file that is not there is a problem, not a silently dead link', () => {
  const { text, problems } = rewrite('one\n[gone](docs/nope.md)')

  assert.equal(problems.length, 1)
  assert.match(problems[0], /README line 2: 'docs\/nope.md' does not exist/)
  assert.equal(text, 'one\n[gone](docs/nope.md)')
})

test('a link that climbs out of the repository is a problem', () => {
  const { problems } = rewrite('[x](../../etc/passwd)')

  assert.match(problems[0], /points outside the repository/)
})

test('a fenced code block and an inline code span are not links', () => {
  const input = 'see `[x](docs/install.md)` here\n\n```\n[Install](docs/install.md)\n```\n\n[Install](docs/install.md)'
  const { text } = rewrite(input)

  assert.match(text, /see `\[x\]\(docs\/install.md\)` here/)
  assert.match(text, /```\n\[Install\]\(docs\/install.md\)\n```/)
  assert.match(text, new RegExp(`\\n\\n\\[Install\\]\\(https://github.com/${repo}/blob/${ref}/docs/install.md\\)$`))
})

test('a link title and a reference-style definition are handled', () => {
  const { text } = rewrite('[i](docs/install.md "Install guide")\n\n[c]: docs/configuration.md')

  assert.match(text, /blob\/[0-9a-f]{40}\/docs\/install.md "Install guide"\)/)
  assert.match(text, /^\[c\]: https:\/\/github.com\//m)
})

test('the real README rewrites with no problems and leaves no relative link behind', () => {
  const readme = fs.readFileSync(path.join(realRoot, 'README.md'), 'utf8')
  const { text, problems } = rewriteReadme(readme, { repo, ref, baseDir: realRoot })

  assert.deepEqual(problems, [])
  assert.ok(text.includes(`blob/${ref}/docs/install.md`))
  // Nothing in a link position is left relative.
  for (const [, target] of text.matchAll(/!?\[[^\]]*\]\(([^)\s]+)/g))
    assert.match(target, /^(https?:|mailto:|#)/, `still relative: ${target}`)
})

test('the command writes the file, pinned to --ref; and fails, writing nothing, on a broken link', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nuget-readme-'))
  try {
    fs.mkdirSync(path.join(dir, 'docs'))
    fs.writeFileSync(path.join(dir, 'docs', 'a.md'), '# a')
    fs.writeFileSync(path.join(dir, 'README.md'), '[a](docs/a.md)')
    const script = path.join(here, 'nuget-readme.mjs')
    const out = path.join(dir, 'out', 'README.md')

    execFileSync('node', [script, '--in', path.join(dir, 'README.md'), '--out', out, '--ref', 'abc123'])
    assert.equal(fs.readFileSync(out, 'utf8'), `[a](https://github.com/${repo}/blob/abc123/docs/a.md)`)

    fs.writeFileSync(path.join(dir, 'README.md'), '[b](docs/missing.md)')
    const broken = path.join(dir, 'out2', 'README.md')
    const result = spawnSync('node', [script, '--in', path.join(dir, 'README.md'), '--out', broken, '--ref', 'abc123'])
    assert.equal(result.status, 1)
    assert.match(result.stderr.toString(), /does not exist/)
    assert.equal(fs.existsSync(broken), false)
  } finally {
    fs.rmSync(dir, { recursive: true, force: true })
  }
})
