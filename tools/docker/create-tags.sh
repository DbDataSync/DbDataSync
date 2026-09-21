#!/usr/bin/env bash
# Phase 163 — turn per-architecture digests into the tags people pull. Called by publish-image.yml's merge job; a script rather than
# inline YAML so it can be run against a throwaway registry (tools/docker/README.md is not a thing — see the phase doc).
#
#   IMAGE=ghcr.io/owner/dbdatasync VERSION=2026.9.20.2152 VARIANT=default|runtime PRERELEASE=true|false OVERWRITE=true|false \
#     tools/docker/create-tags.sh <directory holding one file per architecture, each containing that image's digest>
#
# Both architectures or nothing: a tag that lacked one would let a host of that architecture pull an image that does not exist
# for it. A version tag is meant to stay put, so an existing one is an error unless OVERWRITE is true. A prerelease gets only its
# exact tag, never the moving one, so `latest` never points at something a plain install would not pick either.

set -euo pipefail

digests="${1:?usage: create-tags.sh <digest directory>}"
: "${IMAGE:?}" "${VERSION:?}" "${VARIANT:?}" "${PRERELEASE:?}" "${OVERWRITE:?}"
platforms=(amd64 arm64)

for arch in "${platforms[@]}"; do
  [ -s "$digests/$arch" ] || { echo "::error::no smoke-tested $arch digest for the $VARIANT image"; exit 1; }
done

case "$VARIANT" in
  default) primary="$VERSION";         moving=latest ;;
  runtime) primary="$VERSION-runtime"; moving=runtime ;;
  *) echo "::error::unknown variant '$VARIANT'"; exit 1 ;;
esac
tags=("$primary")
[ "$PRERELEASE" = true ] || tags+=("$moving")

if [ "$OVERWRITE" != true ] && docker buildx imagetools inspect "$IMAGE:$primary" >/dev/null 2>&1; then
  echo "::error::$IMAGE:$primary already exists. A version tag is meant to stay put; re-run with overwrite=true to replace it."
  exit 1
fi

args=()
for tag in "${tags[@]}"; do args+=(-t "$IMAGE:$tag"); done
sources=()
for arch in "${platforms[@]}"; do
  source="$IMAGE@$(cat "$digests/$arch")"
  # Before anything is tagged: each digest must really be the architecture it was handed over as. Found by testing this script
  # against a local registry — checking only after `create` left a tag published for an image that lacked a platform.
  # --format rather than grepping the text: a plain manifest prints no "Platform:" line at all, an index does, and this reads
  # the same answer from both.
  [ "$(docker buildx imagetools inspect --format '{{.Image.OS}}/{{.Image.Architecture}}' "$source")" = "linux/$arch" ] \
    || { echo "::error::$source is not a linux/$arch image"; exit 1; }
  sources+=("$source")
done

docker buildx imagetools create "${args[@]}" "${sources[@]}"

# What a puller will get: both platforms behind the one tag.
manifest="$(docker buildx imagetools inspect "$IMAGE:$primary")"
echo "$manifest"
for arch in "${platforms[@]}"; do
  echo "$manifest" | grep -q "Platform:.*linux/$arch" || { echo "::error::$IMAGE:$primary has no linux/$arch manifest"; exit 1; }
done

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### $VARIANT image"
    for tag in "${tags[@]}"; do echo "- \`docker pull $IMAGE:$tag\`"; done
  } >> "$GITHUB_STEP_SUMMARY"
fi
echo "TAGGED: ${tags[*]}"
