#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT="${1:-$ROOT/artifacts/lidarr-hardlink-import}"
IMAGE="${NATIVE_BUILD_IMAGE:-alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc}"

if [[ "$(uname -m)" != "x86_64" ]]; then
  printf 'The verified v1 native helper build currently supports linux-x64 only.\n' >&2
  exit 2
fi
mkdir -p "$(dirname "$OUTPUT")"
docker run --rm \
  -e OUTPUT_NAME="$(basename "$OUTPUT")" \
  -v "$ROOT:/src:ro" \
  -v "$(dirname "$OUTPUT"):/out" \
  "$IMAGE" sh -lc '
    set -eu
    apk add --no-cache gcc=13.2.1_git20240309-r1 musl-dev=1.2.5-r4 linux-headers=6.6-r0 >/dev/null
    cc -std=c11 -Os -Wall -Wextra -Werror -static -fPIE -pie \
      /src/native/lidarr-hardlink-import.c -o "/out/$OUTPUT_NAME"
    strip "/out/$OUTPUT_NAME"
    chmod 0555 "/out/$OUTPUT_NAME"
  '
file "$OUTPUT"
