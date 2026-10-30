#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT HUP INT TERM
HELPER="$TMP/lidarr-hardlink-import"
cc -std=c11 -O2 -Wall -Wextra -Werror -DAIIMPORT_TEST_HOOKS "$ROOT/native/lidarr-hardlink-import.c" -o "$HELPER"
fail() { printf '%s\n' "FAIL: $*" >&2; exit 1; }
wait_marker() { i=0; while [ ! -e "$1.ready" ]; do i=$((i+1)); [ "$i" -lt 5000 ] || fail "timed out waiting for $1.ready"; sleep 0.001; done; }
AUTH="$TMP/authorizations"
MARKERS="$TMP/authorization-markers"
mkdir -m 0700 "$AUTH" "$MARKERS"
authorize() {
    key=$(printf '%s\n%s' "$1" "$2" | sha256sum | cut -d' ' -f1)
    [ -e "$MARKERS/$key" ] && return 0
    record="$MARKERS/$key.record"
    destination_root=${3:-$(dirname -- "$2")}
    python3 - "$1" "$destination_root" "$record" > "$MARKERS/$key" <<'PY'
import os, secrets, struct, sys, time
source, destination_root, output = sys.argv[1:]
identity = os.stat(source, follow_symlinks=False)
authorization = secrets.token_bytes(32)
path = source.encode('utf-8')
root = destination_root.encode('utf-8')
record = struct.pack(
    '<8sI32sqqQQqqqqqIIIIIIII', b'AIAUTH02', 2, authorization,
    int(time.time()), int(time.time()) + 600, identity.st_dev, identity.st_ino,
    identity.st_size, identity.st_mtime_ns // 1_000_000_000, identity.st_mtime_ns % 1_000_000_000,
    identity.st_ctime_ns // 1_000_000_000, identity.st_ctime_ns % 1_000_000_000,
    identity.st_mode & 0o7777, identity.st_uid, identity.st_gid, 5, 10, 1001, len(path), len(root)) + path + root
name = authorization.hex() + '.approved'
with open(output, 'wb') as stream:
    stream.write(record)
print(name)
PY
    "$HELPER" --publish-approved "$AUTH" "$(cat "$MARKERS/$key")" < "$record" >/dev/null
    rm -f "$record"
}
run_helper() {
    authorize "$1" "$2" "${3:-$(dirname -- "$2")}" 
    AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
        lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
        lidarr_sourcepath=$1 lidarr_destinationpath=$2 "$HELPER" "$1" "$2"
}

mkdir -p "$TMP/source" "$TMP/library"
printf 'original payload\n' > "$TMP/source/track.flac"
identity_json=$($HELPER --inspect "$TMP/source/track.flac")
printf '%s' "$identity_json" | grep -q '"inode":' || fail "inspection did not report an inode"
if $HELPER --inspect "$TMP/source/source-link-before-create" >/dev/null 2>&1; then fail "inspection accepted a missing path"; fi
source_hash=$(sha256sum "$TMP/source/track.flac" | cut -d' ' -f1)
source_before=$(stat -c '%d:%i:%s:%Y:%a:%u:%g:%h' "$TMP/source/track.flac")
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/track.flac" lidarr_destinationpath="$TMP/library/unauthorized.flac" \
    "$HELPER" "$TMP/source/track.flac" "$TMP/library/unauthorized.flac"; then
    fail "transfer without authorization succeeded"
fi
run_helper "$TMP/source/track.flac" "$TMP/library/track.flac"
[ "$(stat -c '%d:%i' "$TMP/source/track.flac")" = "$(stat -c '%d:%i' "$TMP/library/track.flac")" ] || fail "same-filesystem import was not a hardlink"
[ "$source_hash" = "$(sha256sum "$TMP/source/track.flac" | cut -d' ' -f1)" ] || fail "source hash changed"
source_after=$(stat -c '%d:%i:%s:%Y:%a:%u:%g:%h' "$TMP/source/track.flac")
[ "${source_before%:*}" = "${source_after%:*}" ] || fail "source metadata changed"
[ "$(stat -c '%h' "$TMP/source/track.flac")" -eq 2 ] || fail "link count did not increase"
track_key=$(printf '%s\n%s' "$TMP/source/track.flac" "$TMP/library/track.flac" | sha256sum | cut -d' ' -f1)
track_authorization=$(cat "$MARKERS/$track_key")
track_authorization=${track_authorization%.approved}
verification=$($HELPER --verify-result "$AUTH" "$track_authorization" "$TMP/library/track.flac")
printf '%s' "$verification" | grep -q '"outcome":1' || fail "durable transfer verification did not report linked outcome"
if "$HELPER" --verify-result "$AUTH" "$track_authorization" "$TMP/library/replay.flac" >/dev/null 2>&1; then
    fail "durable transfer verification accepted the wrong destination"
fi
run_helper "$TMP/source/track.flac" "$TMP/library/track.flac" || fail "same-inode retry was not idempotent"
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/track.flac" lidarr_destinationpath="$TMP/library/replay.flac" \
    "$HELPER" "$TMP/source/track.flac" "$TMP/library/replay.flac"; then
    fail "claimed authorization replayed to a different destination"
fi
[ ! -e "$TMP/library/replay.flac" ] || fail "destination replay created a file"

printf 'root escape source\n' > "$TMP/source/root-escape.flac"
authorize "$TMP/source/root-escape.flac" "$TMP/outside-root.flac" "$TMP/library"
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/root-escape.flac" lidarr_destinationpath="$TMP/outside-root.flac" \
    "$HELPER" "$TMP/source/root-escape.flac" "$TMP/outside-root.flac"; then
    fail "authorization escaped its destination root"
fi
[ ! -e "$TMP/outside-root.flac" ] || fail "destination-root escape created a file"

printf 'wrong ID source\n' > "$TMP/source/wrong-id.flac"
authorize "$TMP/source/wrong-id.flac" "$TMP/library/wrong-id.flac"
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=999 \
    lidarr_sourcepath="$TMP/source/wrong-id.flac" lidarr_destinationpath="$TMP/library/wrong-id.flac" \
    "$HELPER" "$TMP/source/wrong-id.flac" "$TMP/library/wrong-id.flac"; then
    fail "mismatched Lidarr track ID succeeded"
fi
[ ! -e "$TMP/library/wrong-id.flac" ] || fail "ID mismatch created a destination"

printf 'expired source\n' > "$TMP/source/expired.flac"
authorize "$TMP/source/expired.flac" "$TMP/library/expired.flac"
expired_key=$(printf '%s\n%s' "$TMP/source/expired.flac" "$TMP/library/expired.flac" | sha256sum | cut -d' ' -f1)
expired_name=$(cat "$MARKERS/$expired_key")
python3 - "$AUTH/$expired_name" <<'PY'
import os, struct, sys, time
fd = os.open(sys.argv[1], os.O_RDWR)
now = int(time.time())
os.pwrite(fd, struct.pack('<q', now - 100), 44)
os.pwrite(fd, struct.pack('<q', now - 1), 52)
os.fsync(fd); os.close(fd)
PY
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/expired.flac" lidarr_destinationpath="$TMP/library/expired.flac" \
    "$HELPER" "$TMP/source/expired.flac" "$TMP/library/expired.flac"; then
    fail "expired authorization succeeded"
fi
[ ! -e "$TMP/library/expired.flac" ] || fail "expired authorization created a destination"
run_helper "$TMP/source/expired.flac" "$TMP/library/expired-retry.flac" || fail "expired authorization permanently blocked replacement"
[ "$(stat -c '%d:%i' "$TMP/source/expired.flac")" = "$(stat -c '%d:%i' "$TMP/library/expired-retry.flac")" ] || fail "replacement authorization did not create a hardlink"

printf 'sentinel\n' > "$TMP/library/collision.flac"
printf 'collision source\n' > "$TMP/source/collision-source.flac"
if run_helper "$TMP/source/collision-source.flac" "$TMP/library/collision.flac"; then fail "different-inode collision succeeded"; fi
[ "$(cat "$TMP/library/collision.flac")" = sentinel ] || fail "collision changed destination"

ln -s "$TMP/source/track.flac" "$TMP/source/source-link.flac"
if run_helper "$TMP/source/source-link.flac" "$TMP/library/from-link.flac"; then fail "source symlink succeeded"; fi
ln -s "$TMP/source" "$TMP/source-parent-link"
if run_helper "$TMP/source-parent-link/track.flac" "$TMP/library/from-parent-link.flac"; then fail "source-parent symlink succeeded"; fi
ln -s "$TMP/library" "$TMP/library-parent-link"
printf 'destination parent source\n' > "$TMP/source/destination-parent-source.flac"
if run_helper "$TMP/source/destination-parent-source.flac" "$TMP/library-parent-link/from-parent-link.flac"; then fail "destination-parent symlink succeeded"; fi

printf 'replacement payload\n' > "$TMP/source/replacement.flac"
printf 'race original\n' > "$TMP/source/race.flac"
hook="$TMP/source-race"
authorize "$TMP/source/race.flac" "$TMP/library/race.flac"
AIIMPORT_TEST_AFTER_SOURCE_OPEN=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/race.flac" lidarr_destinationpath="$TMP/library/race.flac" \
    "$HELPER" "$TMP/source/race.flac" "$TMP/library/race.flac" & pid=$!
wait_marker "$hook"
mv "$TMP/source/race.flac" "$TMP/source/race-original.flac"
cp "$TMP/source/replacement.flac" "$TMP/source/race.flac"
: > "$hook.go"
if wait "$pid"; then fail "source replacement race succeeded"; fi
[ ! -e "$TMP/library/race.flac" ] || fail "source replacement race created a destination"
[ "$(stat -c '%d:%i' "$TMP/source/race-original.flac")" != "$(stat -c '%d:%i' "$TMP/source/race.flac")" ] || fail "source replacement fixture did not replace the inode"

printf 'claim original payload\n' > "$TMP/source/claimed-ctime.flac"
hook="$TMP/claimed-ctime"
authorize "$TMP/source/claimed-ctime.flac" "$TMP/library/claimed-ctime.flac"
claimed_mtime_ns=$(python3 - "$TMP/source/claimed-ctime.flac" <<'PY'
import os, sys
print(os.stat(sys.argv[1], follow_symlinks=False).st_mtime_ns)
PY
)
AIIMPORT_TEST_BEFORE_LINK=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/claimed-ctime.flac" lidarr_destinationpath="$TMP/library/claimed-ctime.flac" \
    "$HELPER" "$TMP/source/claimed-ctime.flac" "$TMP/library/claimed-ctime.flac" & pid=$!
wait_marker "$hook"
kill -KILL "$pid"
if wait "$pid" 2>/dev/null; then fail "pre-link claim interruption unexpectedly succeeded"; fi
python3 - "$TMP/source/claimed-ctime.flac" "$claimed_mtime_ns" <<'PY'
import os, sys
path, mtime_ns = sys.argv[1], int(sys.argv[2])
stat = os.stat(path, follow_symlinks=False)
replacement = b'claim changed! payload\n'
assert len(replacement) == stat.st_size
with open(path, 'r+b', buffering=0) as stream:
    stream.write(replacement)
os.utime(path, ns=(stat.st_atime_ns, mtime_ns), follow_symlinks=False)
PY
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/claimed-ctime.flac" lidarr_destinationpath="$TMP/library/claimed-ctime.flac" \
    "$HELPER" "$TMP/source/claimed-ctime.flac" "$TMP/library/claimed-ctime.flac"; then
    fail "claimed pre-link authorization accepted changed ctime"
fi
[ ! -e "$TMP/library/claimed-ctime.flac" ] || fail "claimed ctime mismatch created a destination"

printf 'destination race source\n' > "$TMP/source/destination-race.flac"
hook="$TMP/destination-race"
authorize "$TMP/source/destination-race.flac" "$TMP/library/destination-race.flac"
AIIMPORT_TEST_BEFORE_LINK=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/destination-race.flac" lidarr_destinationpath="$TMP/library/destination-race.flac" \
    "$HELPER" "$TMP/source/destination-race.flac" "$TMP/library/destination-race.flac" & pid=$!
wait_marker "$hook"
printf 'race sentinel\n' > "$TMP/library/destination-race.flac"
: > "$hook.go"
if wait "$pid"; then fail "destination race succeeded"; fi
[ "$(cat "$TMP/library/destination-race.flac")" = "race sentinel" ] || fail "destination race changed sentinel"

printf 'parent rename source\n' > "$TMP/source/parent-rename.flac"
mkdir "$TMP/library/rename-parent"
hook="$TMP/parent-rename"
authorize "$TMP/source/parent-rename.flac" "$TMP/library/rename-parent/track.flac" "$TMP/library/rename-parent"
AIIMPORT_TEST_AFTER_LINK=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/parent-rename.flac" lidarr_destinationpath="$TMP/library/rename-parent/track.flac" \
    "$HELPER" "$TMP/source/parent-rename.flac" "$TMP/library/rename-parent/track.flac" & pid=$!
wait_marker "$hook"
mv "$TMP/library/rename-parent" "$TMP/library/renamed-parent"
mkdir "$TMP/library/rename-parent"
: > "$hook.go"
if wait "$pid"; then fail "destination-parent rename after link succeeded"; fi
[ ! -e "$TMP/library/rename-parent/track.flac" ] || fail "parent rename wrote at the replacement path"
[ -e "$TMP/library/renamed-parent/track.flac" ] || fail "parent rename lost the created hardlink"
[ "$(stat -c '%d:%i' "$TMP/source/parent-rename.flac")" = "$(stat -c '%d:%i' "$TMP/library/renamed-parent/track.flac")" ] || fail "parent rename retained an unrelated file"

printf 'rollback replacement source\n' > "$TMP/source/rollback-replacement.flac"
mkdir "$TMP/library/rollback-parent"
hook="$TMP/rollback-replacement"
after_hook="$TMP/rollback-replacement-after-link"
authorize "$TMP/source/rollback-replacement.flac" "$TMP/library/rollback-parent/track.flac" "$TMP/library/rollback-parent"
AIIMPORT_TEST_AFTER_LINK=$after_hook AIIMPORT_TEST_BEFORE_ROLLBACK=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/rollback-replacement.flac" lidarr_destinationpath="$TMP/library/rollback-parent/track.flac" \
    "$HELPER" "$TMP/source/rollback-replacement.flac" "$TMP/library/rollback-parent/track.flac" & pid=$!
wait_marker "$after_hook"
mv "$TMP/library/rollback-parent" "$TMP/library/rollback-parent-moved"
mkdir "$TMP/library/rollback-parent"
: > "$after_hook.go"
wait_marker "$hook"
rm "$TMP/library/rollback-parent-moved/track.flac"
printf 'rollback replacement sentinel\n' > "$TMP/library/rollback-parent-moved/track.flac"
: > "$hook.go"
if wait "$pid"; then fail "rollback replacement race succeeded"; fi
[ "$(cat "$TMP/library/rollback-parent-moved/track.flac")" = "rollback replacement sentinel" ] || fail "rollback deleted an unrelated replacement"

printf 'post-check rollback source\n' > "$TMP/source/post-check-rollback.flac"
mkdir "$TMP/library/post-check-parent"
after_hook="$TMP/post-check-after-link"
check_hook="$TMP/post-check-rollback-check"
authorize "$TMP/source/post-check-rollback.flac" "$TMP/library/post-check-parent/track.flac" "$TMP/library/post-check-parent"
AIIMPORT_TEST_AFTER_LINK=$after_hook AIIMPORT_TEST_AFTER_ROLLBACK_CHECK=$check_hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/post-check-rollback.flac" lidarr_destinationpath="$TMP/library/post-check-parent/track.flac" \
    "$HELPER" "$TMP/source/post-check-rollback.flac" "$TMP/library/post-check-parent/track.flac" & pid=$!
wait_marker "$after_hook"
mv "$TMP/library/post-check-parent" "$TMP/library/post-check-parent-moved"
mkdir "$TMP/library/post-check-parent"
: > "$after_hook.go"
wait_marker "$check_hook"
rm "$TMP/library/post-check-parent-moved/track.flac"
printf 'post-check replacement sentinel\n' > "$TMP/library/post-check-parent-moved/track.flac"
: > "$check_hook.go"
if wait "$pid"; then fail "post-check rollback replacement race succeeded"; fi
[ -f "$TMP/library/post-check-parent-moved/track.flac" ] || fail "post-check rollback deleted an unrelated replacement"
[ "$(cat "$TMP/library/post-check-parent-moved/track.flac")" = "post-check replacement sentinel" ] || fail "post-check rollback changed an unrelated replacement"

printf 'destination replacement source\n' > "$TMP/source/destination-replacement.flac"
hook="$TMP/destination-replacement"
authorize "$TMP/source/destination-replacement.flac" "$TMP/library/destination-replacement.flac"
AIIMPORT_TEST_AFTER_LINK=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/destination-replacement.flac" lidarr_destinationpath="$TMP/library/destination-replacement.flac" \
    "$HELPER" "$TMP/source/destination-replacement.flac" "$TMP/library/destination-replacement.flac" & pid=$!
wait_marker "$hook"
rm "$TMP/library/destination-replacement.flac"
printf 'replacement sentinel\n' > "$TMP/library/destination-replacement.flac"
: > "$hook.go"
if wait "$pid"; then fail "replaced destination published a successful result"; fi
[ "$(cat "$TMP/library/destination-replacement.flac")" = "replacement sentinel" ] || fail "destination replacement changed sentinel"

printf 'existing result source\n' > "$TMP/source/existing-result.flac"
ln "$TMP/source/existing-result.flac" "$TMP/library/existing-result.flac"
hook="$TMP/existing-result"
authorize "$TMP/source/existing-result.flac" "$TMP/library/existing-result.flac"
AIIMPORT_TEST_BEFORE_EXISTING_RESULT=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/existing-result.flac" lidarr_destinationpath="$TMP/library/existing-result.flac" \
    "$HELPER" "$TMP/source/existing-result.flac" "$TMP/library/existing-result.flac" & pid=$!
wait_marker "$hook"
rm "$TMP/library/existing-result.flac"
printf 'existing replacement sentinel\n' > "$TMP/library/existing-result.flac"
: > "$hook.go"
if wait "$pid"; then fail "replaced existing destination published a successful result"; fi
[ "$(cat "$TMP/library/existing-result.flac")" = "existing replacement sentinel" ] || fail "existing destination replacement changed sentinel"

printf 'interrupted source\n' > "$TMP/source/interrupted.flac"
hook="$TMP/interrupted"
authorize "$TMP/source/interrupted.flac" "$TMP/library/interrupted.flac"
AIIMPORT_TEST_AFTER_LINK=$hook AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy \
    lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/interrupted.flac" lidarr_destinationpath="$TMP/library/interrupted.flac" \
    "$HELPER" "$TMP/source/interrupted.flac" "$TMP/library/interrupted.flac" & pid=$!
wait_marker "$hook"
kill -KILL "$pid"
if wait "$pid" 2>/dev/null; then fail "SIGKILL test unexpectedly succeeded"; fi
[ "$(stat -c '%d:%i' "$TMP/source/interrupted.flac")" = "$(stat -c '%d:%i' "$TMP/library/interrupted.flac")" ] || fail "interrupted hardlink missing"
run_helper "$TMP/source/interrupted.flac" "$TMP/library/interrupted.flac" || fail "interrupted retry was not idempotent"

printf 'cross-device source\n' > "$TMP/source/cross-device.flac"
if [ "$(stat -c '%d' "$TMP/source/cross-device.flac")" != "$(stat -c '%d' /dev/shm)" ]; then
    cross="/dev/shm/lidarr-ai-import-native-test-$$"
    rm -f "$cross"
    if run_helper "$TMP/source/cross-device.flac" "$cross"; then rm -f "$cross"; fail "cross-device hardlink succeeded"; fi
    [ ! -e "$cross" ] || fail "cross-device failure left a destination"
fi

if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=Move lidarr_sourcepath="$TMP/source/track.flac" lidarr_destinationpath="$TMP/library/deferred.flac" \
    "$HELPER" "$TMP/source/track.flac" "$TMP/library/deferred.flac"; then fail "non-hardlink mode did not defer"; else [ "$?" -eq 3 ] || fail "non-hardlink mode returned wrong code"; fi
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_sourcepath="$TMP/source/track.flac" lidarr_destinationpath="$TMP/library/missing-mode.flac" \
    "$HELPER" "$TMP/source/track.flac" "$TMP/library/missing-mode.flac"; then
    fail "missing transfer mode succeeded"
else
    code=$?
    [ "$code" -ne 3 ] || fail "missing transfer mode deferred to unsafe Lidarr transfer"
fi
if AIIMPORT_AUTHORIZATION_DIR="$AUTH" lidarr_transfermode=HardLinkOrCopy lidarr_artist_id=5 lidarr_album_id=10 lidarr_trackfile_trackids=1001 \
    lidarr_sourcepath="$TMP/source/track.flac" lidarr_destinationpath="$TMP/library/mismatch.flac" \
    "$HELPER" "$TMP/source/replacement.flac" "$TMP/library/mismatch.flac"; then fail "argument mismatch succeeded"; fi

printf '%s\n' 'Native descriptor-bound hardlink helper tests passed'
