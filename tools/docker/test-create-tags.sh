#!/usr/bin/env bash
# Phase 163 — exercises create-tags.sh against a throwaway local registry, with real amd64 and arm64 images (a FROM scratch image
# needs no emulation to build for another architecture). Needs docker with buildx; pulls registry:2 and moby/buildkit once.
#
#   tools/docker/test-create-tags.sh
#
# Not run by CI: it is the record of how the tagging logic was verified, and what to re-run when it changes.

set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
port=5055
image="localhost:$port/dbdatasync"
failures=0

cleanup() { docker rm -f dbdatasync-test-registry >/dev/null 2>&1; docker buildx rm dbdatasync-test-builder >/dev/null 2>&1; docker buildx use default >/dev/null 2>&1; rm -rf "$work"; }
trap cleanup EXIT

docker run -d --name dbdatasync-test-registry -p "127.0.0.1:$port:5000" registry:2 >/dev/null
docker buildx create --name dbdatasync-test-builder --driver docker-container --driver-opt network=host --use >/dev/null
sleep 3

cd "$work" || exit 1
echo hi > f
printf 'FROM scratch\nCOPY f /f\n' > Dockerfile

push() { # push <arch> <provenance true|false> -> prints the digest
  docker buildx build --builder dbdatasync-test-builder --platform "linux/$1" --provenance="$2" \
    --output "type=image,name=$image,push-by-digest=true,name-canonical=true,push=true" --metadata-file m.json . >/dev/null 2>&1
  python3 -c "import json;print(json.load(open('m.json'))['containerimage.digest'])"
}
tags() { curl -s "http://localhost:$port/v2/dbdatasync/tags/list"; }
check() { # check <description> <expected exit 0|1> <output must contain> -- <env...>
  local description="$1" want="$2" contains="$3"; shift 4
  local out code
  out="$(env IMAGE="$image" "$@" "$here/create-tags.sh" "$DIGESTS" 2>&1)"; code=$?
  if { [ "$want" = ok ] && [ $code -eq 0 ]; } || { [ "$want" = fail ] && [ $code -ne 0 ]; }; then
    if echo "$out" | grep -q -- "$contains"; then echo "PASS  $description"; return; fi
  fi
  echo "FAIL  $description"; echo "$out" | tail -5; failures=$((failures + 1))
}

mkdir good bad missing
push amd64 true > good/amd64; push arm64 true > good/arm64   # provenance indexes, as CI pushes them
push amd64 false > bad/amd64; cp bad/amd64 bad/arm64         # an amd64 image handed over as arm64
cp good/amd64 missing/amd64

DIGESTS="$work/good"
# First, so that "latest" not existing afterwards can only mean the prerelease did not create it.
check "a prerelease gets only its exact tag" ok "TAGGED: 2026.10.2.1-beta$" -- VERSION=2026.10.2.1-beta VARIANT=default PRERELEASE=true OVERWRITE=false
if tags | grep -q '"latest"\|"runtime"'; then echo "FAIL  a prerelease moved a moving tag"; failures=$((failures + 1)); else echo "PASS  …and no moving tag exists yet"; fi
check "a stable default release gets its version and latest" ok "TAGGED: 2026.10.1.1 latest" -- VERSION=2026.10.1.1 VARIANT=default PRERELEASE=false OVERWRITE=false
check "a stable runtime release gets its version-runtime and runtime" ok "TAGGED: 2026.10.1.1-runtime runtime" -- VERSION=2026.10.1.1 VARIANT=runtime PRERELEASE=false OVERWRITE=false
check "an existing version tag is refused" fail "already exists" -- VERSION=2026.10.1.1 VARIANT=default PRERELEASE=false OVERWRITE=false
check "…unless overwrite is asked for" ok "TAGGED" -- VERSION=2026.10.1.1 VARIANT=default PRERELEASE=false OVERWRITE=true
DIGESTS="$work/missing"
check "a missing architecture is refused" fail "no smoke-tested arm64 digest" -- VERSION=2026.10.3.1 VARIANT=default PRERELEASE=false OVERWRITE=false
DIGESTS="$work/bad"
check "a digest that is not the architecture it was handed over as is refused" fail "is not a linux/arm64 image" -- VERSION=2026.10.4.1 VARIANT=default PRERELEASE=false OVERWRITE=false
tags | grep -q '2026.10.4.1' && { echo "FAIL  a tag was created for the refused pair"; failures=$((failures + 1)); } || echo "PASS  …and nothing was tagged for it"
DIGESTS="$work/good"
check "an unknown variant is refused" fail "unknown variant" -- VERSION=2026.10.5.1 VARIANT=other PRERELEASE=false OVERWRITE=false

echo
if [ $failures -eq 0 ]; then echo "all passed"; else echo "$failures failed"; exit 1; fi
