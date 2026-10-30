# Verification record

## Supported target

- Lidarr: **3.1.2.4913 only**
- Runtime: Linux / .NET 8
- Architecture: standalone in-process plugin; no companion service or separate Lidarr credentials
- Transfer contract: sealed native hardlinks only; copy fallback is forbidden

## Build and automated tests

Verified on 2026-10-30 UTC:

```text
Native descriptor-bound hardlink helper tests passed
Lidarr.AIImport.Companion.Tests: 100 passed, 0 failed
Lidarr.Plugin.AIImport.Tests: 89 passed, 0 failed
Build: 0 warnings, 0 errors
```

The native suite covers exact authorization matching, replay, expiry, source replacement, symlinks, destination conflicts, cross-device failure, destination-parent rename, post-link replacement, no-delete handling for replacement races, durable-result reconciliation, and missing/mismatched Lidarr transfer context. Managed tests also verify that installation preserves an occupied host descriptor rather than overwriting it.

The release ZIP is constrained to four entries:

- `Lidarr.Plugin.AIImport.dll`
- `lidarr-hardlink-import`
- `LICENSE`
- `SOURCE.txt`

No companion binary, companion configuration, runtime manifest, separate Lidarr credentials, or debug symbols are packaged.

## Disposable full-plugin verification

Verified against a disposable `ghcr.io/linuxserver-labs/prarr:lidarr-plugins` container running Lidarr `3.1.2.4913`, with the clean package installed and the sealed helper active at `/proc/self/fd/4095`.

A real blackhole download containing ten generated FLAC files reached Lidarr's `importBlocked` state. The authenticated notification-provider action route completed:

```text
discover → approve → execute → status → reconcile → Succeeded
```

Observed production-path results:

- Discovery returned one complete, validated ten-file/ten-track proposal.
- Approval passed Lidarr's authoritative manual-import revalidation.
- Execution submitted a real `ManualImport` command; Lidarr reported `Manually imported 10 files`.
- Reconciliation verified all ten files using native transfer results, filesystem identity, Lidarr import history, track-file records, and track bindings.
- Final workflow state was `Succeeded` with `verifiedFiles: 10`.
- Lidarr contained exactly ten matching track-file records, ten track bindings, and ten matching import-history records.

## Filesystem and source-preservation proof

For every imported file:

- The source remained present for seeding.
- Source device, inode, size, and mode were unchanged.
- Destination device and inode equaled the source device and inode.
- Source and destination link counts were `2`.
- Source and destination sizes and SHA-256 hashes were equal.
- No copy fallback occurred.

All queue, filesystem, database, and restart operations used disposable container paths. No real library was mounted or modified.

## Release provenance gate

Release artifacts are built only from a clean tagged tree. `SOURCE.txt` must identify the exact release commit, the sibling `.sha256` must verify, and the published ZIP must be downloaded and read back before the release is declared usable.
