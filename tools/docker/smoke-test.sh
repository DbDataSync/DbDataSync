#!/usr/bin/env bash
# Phase 163 — the checks a container image has to pass before anyone can pull it.
#
#   tools/docker/smoke-test.sh <image> [expected-version]
#
# Runs the image, waits for its own health check, and then asks it the questions that have each gone wrong in this project
# without any unit test noticing: does it report the version it was built for (a published image that says "alpha" is lying to
# `dbdatasync update`), does it serve its web console and docs to someone who has not signed in (with authentication on, the
# default, it once answered 401 to `/`), is the API still closed, and did any native library fail to load (a wrong-architecture
# .so shows up as a DllNotFoundException at run time, which nothing else catches until a person runs the image).
#
# Run from the repository root: the docs and pictures the image must serve are read from docs/ to know what to ask for.

set -euo pipefail

image="${1:?usage: smoke-test.sh <image> [expected-version]}"
expected_version="${2:-}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
name="dbdatasync-smoke-$$"

fail() { echo "SMOKE TEST FAILED: $*" >&2; docker logs "$name" 2>&1 | tail -40 >&2 || true; exit 1; }
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "== starting $image"
docker run -d --name "$name" -p 127.0.0.1::8080 "$image" >/dev/null
port="$(docker port "$name" 8080/tcp | head -n1 | sed 's/.*://')"
[ -n "$port" ] || fail "no published port"
base="http://127.0.0.1:$port"

echo "== waiting for the container's own health check"
healthy=false
for _ in $(seq 1 45); do
  if docker exec "$name" dotnet /app/DbDataSync.Cli.dll health --url http://127.0.0.1:8080 >/dev/null 2>&1; then healthy=true; break; fi
  [ "$(docker inspect -f '{{.State.Running}}' "$name")" = "true" ] || fail "the container exited"
  sleep 2
done
$healthy || fail "never became healthy"

echo "== architecture: $(docker exec "$name" uname -m)"

if [ -n "$expected_version" ]; then
  reported="$(docker exec "$name" dotnet /app/DbDataSync.Cli.dll version)"
  echo "== version: $reported"
  case "$reported" in
    "$expected_version"*) ;;
    *) fail "reports '$reported', expected it to start with '$expected_version'" ;;
  esac
fi

status() { curl -s -o /dev/null -w '%{http_code}' "$base$1"; }
expect() { # expect <path> <status> [content-type prefix]
  local got type
  got="$(status "$1")"
  [ "$got" = "$2" ] || fail "GET $1 answered $got, expected $2"
  if [ -n "${3:-}" ]; then
    type="$(curl -s -o /dev/null -w '%{content_type}' "$base$1")"
    case "$type" in "$3"*) ;; *) fail "GET $1 was '$type', expected '$3…'" ;; esac
  fi
  echo "   $2 $1"
}

echo "== web console and docs, for someone who has not signed in"
body="$(curl -s "$base/")"
case "$body" in *"No web assets are published"*) fail "/ answered the no-web-assets message, not the console" ;; esac
expect / 200 text/html
expect /invite 200 text/html
for page in "$root"/docs/*.md; do expect "/docs/$(basename "$page")" 200 text/markdown; done
while IFS= read -r picture; do
  [ -z "$picture" ] || expect "/$picture" 200 image/png
done < <(tr -d '\r' < "$root/docs/images.txt")

echo "== the API stays closed"
expect /api/connections 401
expect /api/about 401

echo "== no native library failed to load"
if docker logs "$name" 2>&1 | grep -E "DllNotFoundException|BadImageFormatException|Unhandled exception|Unable to load shared library"; then
  fail "the log shows a native or unhandled failure"
fi

echo "SMOKE TEST PASSED: $image"
