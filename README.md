# Lidarr AI Import Plugin

A safety-first Lidarr plugin foundation for OpenAI-compatible, metadata-constrained import matching.

> **Current development status:** Phase 2 read-only discovery is merged. Phase 3 now includes a packaged hardlink-only import script, executable-mode repair on plugin startup, and an authenticated read-only prerequisite assessment. Approval/import execution remains disabled.

## Why the initial release is read-only

Lidarr's ordinary `Copy` path uses `HardLinkOrCopy`: when hardlink creation fails it silently falls back to a copy. The Phase 3 script demonstrates interception using only `ln`, device/inode verification, and no defer-on-failure behavior. Independent review found unavoidable pathname TOCTOU limitations in the shell implementation, so it is not an execution-ready security boundary. Execution remains disabled until a descriptor-safe transfer implementation and fail-closed approval/reconciliation are verified.

See [docs/FEASIBILITY.md](docs/FEASIBILITY.md) for source-backed findings, [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the staged design, and [docs/VERIFICATION.md](docs/VERIFICATION.md) for the disposable-container test record.

## Compatibility

- Tested image: `ghcr.io/linuxserver-labs/prarr:lidarr-plugins`
- Tested image package: Lidarr `3.1.2.4913`, plugin branch commit `e42a7ca4fd633e021d69da7daa0368b870b0282e`
- Target framework: .NET 8 (`net8.0`)
- Designed to coexist with plugins such as Tubifarry by using Lidarr's isolated plugin loader and existing Connect-provider settings UI; direct coexistence testing is still pending.
- Other Lidarr releases are not yet claimed compatible. Plugin internals are not a stable SDK.

## Install

### From a GitHub release

1. Use the plugin-enabled image shown above.
2. In Lidarr, open **System → Plugins**.
3. Install `https://github.com/mshihadeh1/Lidarr.Plugin.AIImport`.
4. Restart Lidarr when prompted.
5. Open **Settings → Connect**, add **AI Import**, configure the endpoint, and click **Test**.

The Lidarr installer selects a release asset whose name contains `net8.0.zip`; release notes must include `Minimum Lidarr Version: 3.1.2.4913`.

### Manual Docker installation

Extract the release files under the persistent `/config/plugins/<owner>/<repository>/` hierarchy:

```text
/config/plugins/mshihadeh1/Lidarr.Plugin.AIImport/Lidarr.Plugin.AIImport.dll
/config/plugins/mshihadeh1/Lidarr.Plugin.AIImport/lidarr-hardlink-import
```

Restart the container. Do not install into `/app/lidarr`; that path is replaced with image updates.

## LLM settings

- API base URL (`http://localhost:8080/v1`, or another reachable URL)
- Optional API key
- Model identifier
- Temperature
- Request timeout
- Maximum output tokens
- Optional custom system prompt

The client uses `POST {baseUrl}/chat/completions` and standard OpenAI chat-completion response fields. Base URLs containing credentials, queries, or fragments are rejected. Local providers require no cloud account. Docker note: `localhost` inside Lidarr means the Lidarr container; use a shared Docker network and service name for another container. HTTP is supported for local/self-hosted endpoints; use HTTPS for any endpoint crossing an untrusted network because bearer keys otherwise travel in plaintext.

Examples are in [examples/openai-compatible-settings.json](examples/openai-compatible-settings.json).

## Companion (read-only)

Build the pinned container from the repository root:

```bash
docker build -f companion/Dockerfile -t lidarr-ai-import-companion:local .
docker run --rm -p 127.0.0.1:8080:8080 \
  -e LIDARR_BASE_URL=http://lidarr:8686 \
  -e LIDARR_API_KEY=replace-with-lidarr-api-key \
  -e COMPANION_API_TOKEN=replace-with-a-random-companion-token \
  -e HARDLINK_SCRIPT_PATH=/config/plugins/mshihadeh1/Lidarr.Plugin.AIImport/lidarr-hardlink-import \
  lidarr-ai-import-companion:local
```

Place the container on the same Docker network as Lidarr and use Lidarr's service name rather than `localhost`. The service exposes only:

- `GET /health` — reports read-only mode and the compatibility pin.
- `GET /api/v1/failed-imports` — scans `importBlocked` and `importFailed` queue items and returns dry-run results. Send the configured token in `X-Companion-Api-Key`; missing or incorrect tokens receive HTTP 401.
- `GET /api/v1/import-safety` — reads Lidarr's media-management and metadata settings and reports whether the hardlink-script prerequisites are satisfied. It always reports `executionEnabled: false` in this phase.

Discovery calls only Lidarr `GET` endpoints and refuses versions other than `3.1.2.4913`. It paginates the queue, requests manual-import metadata by download ID, strictly verifies positive IDs and source/artist/album/release/track relationships through Lidarr, excludes tracks that already have files, rejects duplicate source identities, and maps every source file one-to-one. Deterministic output passes through the same strict proposal validator used for untrusted model output. `proposed` means only that a verified metadata mapping was found; Lidarr rejection reasons remain in the response and no importability claim is made. Ambiguous or incomplete results are returned as `manualReview`; Phase 2 does not call an LLM or expose approval/import endpoints. Keep the service bound to localhost even though API routes require authentication, because discovery responses contain library metadata.

Lidarr's script-import setting is global. If the packaged script is configured, every Lidarr transfer requested as `HardLinkOrCopy` becomes strict hardlink-or-fail, including imports not initiated by this project. This deliberately prevents silent copying but can make unrelated cross-device imports fail.

## Security and privacy

- API keys use Lidarr's secret field treatment and are never included in prompts.
- The current plugin sends only a fixed connection-test prompt.
- Match proposals use opaque source-file IDs rather than full paths.
- LLM IDs are proposals only; future matching must verify every artist, album, release, and track ID against Lidarr/MusicBrainz candidates.
- Unknown or duplicate JSON fields, malformed responses, invented statuses, missing IDs, duplicate file IDs, and invalid confidence values are rejected. The runtime parser is authoritative; the published JSON Schema cannot detect duplicate JSON object keys or uniqueness by only `sourceFileId`.

## Build and test

```bash
git clone --recurse-submodules https://github.com/mshihadeh1/Lidarr.Plugin.AIImport.git
cd Lidarr.Plugin.AIImport
./scripts/build.sh
```

The script needs Docker, runs both test projects, produces `artifacts/Lidarr.Plugin.AIImport-v0.1.0.net8.0.zip`, and publishes the companion under `artifacts/companion/`. It pins Lidarr source to the target image's plugin commit.

## Roadmap

- **Phase 1:** feasibility, loadable plugin, settings, Test Connection — implemented.
- **Phase 2:** read-only failed-import discovery, deterministic candidate generation, Lidarr/MusicBrainz-ID verification, dry-run proposals — merged.
- **Phase 3:** hardlink-only transfer primitive and read-only safety assessment — implemented locally; approval/import execution remains gated pending fail-closed reconciliation.
- **Phase 4:** batch UX and opt-in automation after sufficient validation evidence.

## License

GPL-3.0-only, matching Lidarr's license because this plugin links against Lidarr internals.
