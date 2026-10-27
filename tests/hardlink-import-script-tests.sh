#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/lidarr-hardlink-import"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"; rm -f /dev/shm/lidarr-ai-import-hardlink-test-*' EXIT

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

run_script() {
  local mode="$1" source="$2" destination="$3"
  lidarr_transfermode="$mode" \
  lidarr_sourcepath="$source" \
  lidarr_destinationpath="$destination" \
    "$SCRIPT" "$source" "$destination"
}

mkdir -p "$TMP/source" "$TMP/library"
printf 'immutable audio bytes\n' > "$TMP/source/track.flac"
source_before="$(sha256sum "$TMP/source/track.flac" | cut -d' ' -f1)"

run_script HardLinkOrCopy "$TMP/source/track.flac" "$TMP/library/track.flac"
[[ -f "$TMP/library/track.flac" ]] || fail "successful run did not create destination"
[[ "$(stat -c '%d:%i' "$TMP/source/track.flac")" == "$(stat -c '%d:%i' "$TMP/library/track.flac")" ]] || fail "destination is not the same inode"
[[ "$source_before" == "$(sha256sum "$TMP/source/track.flac" | cut -d' ' -f1)" ]] || fail "source content changed"

printf 'existing destination\n' > "$TMP/library/existing.flac"
if run_script HardLinkOrCopy "$TMP/source/track.flac" "$TMP/library/existing.flac"; then
  fail "existing destination was overwritten"
fi
[[ "$(cat "$TMP/library/existing.flac")" == "existing destination" ]] || fail "existing destination changed"

ln -s "$TMP/source/track.flac" "$TMP/source/symlink.flac"
if run_script HardLinkOrCopy "$TMP/source/symlink.flac" "$TMP/library/from-symlink.flac"; then
  fail "symlink source was accepted"
fi
[[ ! -e "$TMP/library/from-symlink.flac" ]] || fail "symlink source created a destination"

ln -s "$TMP/library/missing-target.flac" "$TMP/library/dangling.flac"
if run_script HardLinkOrCopy "$TMP/source/track.flac" "$TMP/library/dangling.flac"; then
  fail "symlink destination was accepted"
fi
[[ -L "$TMP/library/dangling.flac" ]] || fail "symlink destination was removed"

if run_script Copy "$TMP/source/track.flac" "$TMP/library/deferred.flac"; then
  fail "non-hardlink transfer mode should return Lidarr defer code 3"
else
  status=$?
  [[ "$status" == 3 ]] || fail "non-hardlink transfer mode returned $status instead of 3"
fi
[[ ! -e "$TMP/library/deferred.flac" ]] || fail "deferred transfer created a file"

cross_device_destination="/dev/shm/lidarr-ai-import-hardlink-test-$$"
if run_script HardLinkOrCopy "$TMP/source/track.flac" "$cross_device_destination"; then
  fail "cross-device hardlink unexpectedly succeeded"
fi
[[ ! -e "$cross_device_destination" ]] || fail "failed cross-device run left a destination"

if lidarr_transfermode=HardLinkOrCopy lidarr_sourcepath="$TMP/source/different.flac" lidarr_destinationpath="$TMP/library/mismatch.flac" \
  "$SCRIPT" "$TMP/source/track.flac" "$TMP/library/mismatch.flac"; then
  fail "argument/environment mismatch was accepted"
fi
[[ ! -e "$TMP/library/mismatch.flac" ]] || fail "mismatch run created a destination"

printf 'Hardlink import script tests passed\n'
