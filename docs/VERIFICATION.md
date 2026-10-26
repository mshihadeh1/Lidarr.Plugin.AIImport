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
