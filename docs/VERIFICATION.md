# Verification record

Verified on 2026-10-26 UTC against a disposable container created from:

- Image: `ghcr.io/linuxserver-labs/prarr:lidarr-plugins`
- Lidarr: `3.1.2.4913`
- Branch: `plugins`
- Runtime: `.NET 8.0.12`

The test did not use or modify an existing Lidarr configuration.

## Build and tests

`./scripts/build.sh` completed with zero build warnings/errors and 25/25 tests passing. It produced:

`artifacts/Lidarr.Plugin.AIImport-v0.1.0.net8.0.zip`

## Host loading

The plugin DLL was placed at:

`/config/plugins/mshihadeh1/Lidarr.Plugin.AIImport/Lidarr.Plugin.AIImport.dll`

After restart:

- `GET /api/v1/system/plugins` reported `AI Import`, owner `mshihadeh1`, installed version `0.1.0.0`.
- `GET /api/v1/notification/schema` reported `AiImportConnector` / `AiImportSettings`.
- The schema exposed all seven expected fields: `apiBaseUrl`, `apiKey`, `model`, `temperature`, `requestTimeoutSeconds`, `maximumOutputTokens`, and `customSystemPrompt`.
- `POST /api/v1/notification/test` executed the connector. An intentionally unreachable endpoint produced the expected sanitized HTTP 400 validation response, with no API key in the response.
- The packaged ZIP contains the plugin DLL, GPL license, and corresponding-source notice; a sibling `.sha256` file is generated for integrity verification.

## Compatibility pin

The target image's `Lidarr.Core.dll` and `Lidarr.Common.dll` assembly versions are both `3.1.2.4913`. The project references are built with that exact assembly version; using Lidarr's default wildcard assembly version caused the disposable host to reject the plugin, and was corrected before release.

## Phase 2 read-only companion

Verified on 2026-10-26 UTC from branch `feat/phase2-readonly`:

- `./scripts/build.sh` passed 59/59 tests: 25 plugin tests and 34 companion tests. The plugin rebuild completed with zero warnings/errors, and the companion was published to `artifacts/companion/`.
- `companion/Dockerfile` built successfully from pinned .NET 8 SDK and ASP.NET runtime image digests.
- Discovery tests cover the exact Lidarr version gate, queue filtering and pagination (including skipped rows), GET-only Lidarr requests, opaque and unique file IDs, strict positive release/track IDs, explicit album/release ownership, release-scoped track queries with matching album ownership, required `hasFile`, mixed-metadata safeguards, exclusion of existing tracks, snapshot-sensitive analysis IDs, exact deterministic proposals, strict validation of deterministic output, authenticated API access, ambiguous/manual-review outcomes, one-to-one assignment, and rejection of invented IDs.
- The built container was exercised end-to-end against a deterministic Lidarr API fixture. It made only five GETs (system status, queue, manual import, album, and track) and returned the verified proposal `artistId=5`, `albumId=10`, `releaseId=101`, `trackId=1001`, with `usedLlm=false`.
- The built companion also ran against the disposable `lidarr-ai-import-smoke` container. `GET /health` reported `read-only` and compatibility `3.1.2.4913`; unauthenticated discovery returned HTTP 401, while authenticated `GET /api/v1/failed-imports` returned HTTP 200 and `[]` for its empty queue.
- Lidarr's debug log recorded only the expected `GET /api/v1/system/status` and `GET /api/v1/queue?...`; a POST to the companion discovery route returned HTTP 405.
- SHA-256 hashes of the disposable container's `lidarr.db` and `lidarr.db-wal` were identical before and after discovery.

The live smoke container had no failed queue item, so deterministic proposal behavior is verified by the fixture-backed full discovery pipeline rather than by mutating Lidarr to manufacture a failure. No real Lidarr data was used or modified.

## Phase 3 hardlink foundation

Verified on 2026-10-26 against the same disposable Lidarr container:

- `./scripts/build.sh` passed 69 managed tests (29 plugin and 40 companion), the shell hardlink integration suite, and a zero-warning/zero-error rebuild.
- Plugin startup repaired the extracted script mode from `0644` to `0755`; this is necessary because Lidarr's ZIP extractor does not restore Unix executable bits.
- The authenticated safety endpoint returned `configurationPrerequisitesSatisfied: true`, `executionEnabled: false`, and no blockers only after every source-mutating setting was disabled and the exact script path was configured. Unauthenticated access returned HTTP 401.
- A real native `ManualImport` of the bundled `nin.flac` fixture created `/music/Nine Inch Nails/nin-hardlink2.flac`. Source and destination had the same device/inode; source SHA-256, size, mtime, mode, UID, and GID were unchanged. Lidarr created the track-file record and `trackFileImported` history.
- A cross-device source under `/dev/shm` caused script exit 24. No destination and no additional track-file record were created; logs showed that copy fallback was forbidden.
- Lidarr nevertheless marked that failed command `completed`. Future orchestration must reconcile track-file and inode state rather than trust command status.
- Independent security review found that the shell implementation cannot eliminate pathname TOCTOU races or make failure cleanup descriptor-safe. The safety response reports this limitation and `executionEnabled` remains `false`; an approval/import route must not be added until a descriptor-safe native helper or pinned Lidarr implementation replaces the shell primitive.

All Phase 3 operations above used only disposable fixture data. Approval/import routes remain absent.

## v1.0 standalone release verification

Verified on 2026-10-29 UTC against a fresh disposable `ghcr.io/linuxserver-labs/prarr:lidarr-plugins` container running Lidarr `3.1.2.4913`:

- `ALLOW_DIRTY_BUILD=1 VERSION=1.0.0 ./scripts/build.sh` passed the native descriptor-bound hardlink suite, 100 retained companion regression tests, and 86 standalone plugin tests. The final managed build completed with zero warnings and zero errors.
- The four-entry verification ZIP contained only `Lidarr.Plugin.AIImport.dll`, `lidarr-hardlink-import`, `LICENSE`, and `SOURCE.txt`. The dirty verification package explicitly made no exact corresponding-source claim.
- The deployed plugin and helper SHA-256 values matched the freshly built artifacts, and Lidarr loaded assembly version `1.0.0.0`.
- A real blackhole queue item containing ten generated FLAC files reached Lidarr's `importBlocked` state. The authenticated provider action route completed `discover → approve → execute → reconcile → status` through the production plugin graph.
- Discovery returned one validated ten-file/ten-track proposal. Approval passed Lidarr's authoritative manual-import revalidation. Execution submitted `ManualImport` command `8404`, which completed successfully with the message `Manually imported 10 files`.
- Reconciliation returned `status: succeeded`, `verifiedFiles: 10`, and verified every approved file against native transfer results, filesystem state, Lidarr history, track-file records, and track bindings. Final workflow status was `succeeded`.
- All ten source files remained present. Before/after source device, inode, size, and mode matched; each destination had the same device and inode as its source, link count `2`, equal size, and equal SHA-256.
- The disposable download client was configured not to remove completed source data. No real library or existing Lidarr instance was mounted or modified.
- Integration defects found during the run were corrected before this verification: action-only provider resolution, unfiltered manual-import discovery, authoritative approval mapping, lowercase Lidarr history-data keys, and isolation of vanished stale queue entries.

The final release artifact must still be rebuilt from the exact clean tagged source so `SOURCE.txt` names that commit. Published release metadata and checksums must be read back before distribution.
