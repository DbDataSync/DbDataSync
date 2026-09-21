#!/usr/bin/env bash
# Dispatch DbDataSync's release.yml, find the run it just created, and watch it to completion.
# See ../SKILL.md for what a successful run means and what to do if it isn't found right away.
set -euo pipefail

REPO="DbDataSync/DbDataSync"
BETA=false

for arg in "$@"; do
  case "$arg" in
    --beta) BETA=true ;;
    -h|--help)
      echo "Usage: $0 [--beta]"
      echo "  --beta   Publish as a SemVer 2 prerelease (hidden from a plain dotnet tool install)."
      exit 0
      ;;
    *)
      echo "Unknown argument: $arg (usage: $0 [--beta])" >&2
      exit 1
      ;;
  esac
done

# release.yml refuses outright unless dispatched against `test` (architecture/branching-and-releases.md
# — a release is cut from test's own validated snapshot, and release.yml fast-forwards `main` to the
# released commit as its own last step; there is no separate test -> main PR or push). This script
# checks against origin/test, not origin/main, for the same reason: a local edit that hasn't been
# committed, or a commit that hasn't reached test yet (dev -> test only promotes after a green CI run),
# would silently be missing from the release with no error anywhere otherwise.
if ! git rev-parse --show-toplevel >/dev/null 2>&1; then
  echo "Not inside a git working tree. Run this from a clone of $REPO." >&2
  exit 1
fi

if [ -n "$(git status --porcelain)" ]; then
  echo "Working tree has uncommitted changes — commit or stash them before releasing:" >&2
  git status --short >&2
  exit 1
fi

git fetch origin test --quiet
local_sha=$(git rev-parse HEAD)
remote_sha=$(git rev-parse origin/test)
if [ "$local_sha" != "$remote_sha" ]; then
  echo "Local HEAD ($local_sha) does not match origin/test ($remote_sha)." >&2
  echo "Push to dev and wait for it to reach test (dev -> test only promotes after a green CI run) before" >&2
  echo "releasing — release.yml builds test, not this checkout, and refuses anything dispatched against" >&2
  echo "another ref." >&2
  exit 1
fi

echo "Dispatching release.yml (beta=$BETA) against $REPO, ref test..."
before=$(date -u +%Y-%m-%dT%H:%M:%SZ)
gh workflow run release.yml --repo "$REPO" --ref test -f beta="$BETA"

# `gh workflow run` doesn't hand back a run id, so find the run it just created: the newest
# workflow_dispatch-triggered Release run created at or after the moment we dispatched. A short
# retry loop covers the few seconds GitHub sometimes takes to list a run that was just queued.
echo "Waiting for the run to appear..."
run_id=""
for _ in $(seq 1 15); do
  run_id=$(gh run list --repo "$REPO" --workflow Release --limit 5 \
    --json databaseId,createdAt,event \
    --jq "[.[] | select(.event == \"workflow_dispatch\" and .createdAt >= \"$before\")] | sort_by(.createdAt) | .[0].databaseId // empty")
  [ -n "$run_id" ] && break
  sleep 2
done

if [ -z "$run_id" ]; then
  echo "Could not find the dispatched run after 30s. Check manually:" >&2
  echo "  gh run list --repo $REPO --workflow Release --limit 5" >&2
  exit 1
fi

echo "Watching run $run_id (https://github.com/$REPO/actions/runs/$run_id)..."
if ! gh run watch "$run_id" --repo "$REPO" --exit-status; then
  status=$?
  echo
  echo "Run failed. See the failing step's log with:"
  echo "  gh run view $run_id --repo $REPO --log-failed"
  exit "$status"
fi

sha=$(gh run view "$run_id" --repo "$REPO" --json headSha --jq .headSha)
echo
echo "Release run succeeded (commit ${sha:0:12})."
echo "Recent releases:"
gh release list --repo "$REPO" --limit 3
