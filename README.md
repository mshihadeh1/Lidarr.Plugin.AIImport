# Lidarr AI Import Plugin

A safety-first Lidarr plugin foundation for OpenAI-compatible, metadata-constrained import matching.

> **Current development status:** v1.0 release candidate. Authenticated approval, strict hardlink execution, durable command receipts, and per-file reconciliation are implemented. This build has not yet completed a real torrent-client recheck; back up Lidarr and test one expendable album before broader use.

## Why execution is fail-closed

Lidarr's ordinary `Copy` path uses `HardLinkOrCopy`: when hardlink creation fails it can fall back to a copy. This project replaces that boundary with a native descriptor-bound helper that either creates the authorized hardlink or fails. Approvals are short-lived and single-use. A submitted import is not successful until native results, source and destination inode identity, Lidarr history, track-file state, and track bindings all reconcile. Unknown outcomes are never retried automatically.

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

The Lidarr installer selects a release asset whose name contains `net8.0.zip`; release notes must include `Minimum Lidarr Version: 3.1.2.4913` for installer compatibility and separately state that `3.1.2.4913` is the exact tested and supported version. Newer releases require separate compatibility verification.

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

## Companion

Build the pinned container from the repository root:

```bash
docker build -f companion/Dockerfile -t lidarr-ai-import-companion:local .
sudo install -d -m 0700 -o 1000 -g 1000 /host/ai-import-data
docker run --rm --name lidarr-ai-import --network media --user 1000:1000 \
  -p 127.0.0.1:8080:8080 \
  -e LIDARR_BASE_URL=http://lidarr:8686 \
  -e LIDARR_API_KEY=replace-with-lidarr-api-key \
  -e COMPANION_API_TOKEN=replace-with-a-random-companion-token \
  -e HARDLINK_SCRIPT_PATH=/config/plugins/mshihadeh1/Lidarr.Plugin.AIImport/lidarr-hardlink-import \
  -e AIIMPORT_AUTHORIZATION_DIR=/data/authorizations \
  -e AUTHORIZATION_DEPLOYMENT_CONFIRMED=true \
  -v /host/ai-import-data:/data \
  -v /host/downloads:/downloads \
  -v /host/music:/music \
  lidarr-ai-import-companion:local
```

Replace `1000:1000` with the UID/GID used by Lidarr. The companion and Lidarr-launched helper must run as the same UID because authorization directories and records use owner-only permissions. Place the container on the same Docker network as Lidarr and use Lidarr's service name rather than `localhost`. Mount `/host/ai-import-data` into Lidarr at `/data` too, set `AIIMPORT_AUTHORIZATION_DIR=/data/authorizations` in **both** processes, and mount download and library paths at the exact same paths in both containers. Set `AUTHORIZATION_DEPLOYMENT_CONFIRMED=true` only after those checks; otherwise the safety endpoint keeps execution disabled. Every `/api` request requires `X-Companion-Api-Key`.

The corresponding Lidarr service settings are:

```yaml
services:
  lidarr:
    user: "1000:1000"
    environment:
      AIIMPORT_AUTHORIZATION_DIR: /data/authorizations
    volumes:
      - /host/ai-import-data:/data
      - /host/downloads:/downloads
      - /host/music:/music
```

- `GET /health` — reports approval-required mode and the compatibility pin.
- `GET /api/v1/failed-imports` — scans `importBlocked` and `importFailed` queue items and returns dry-run results. Send the configured token in `X-Companion-Api-Key`; missing or incorrect tokens receive HTTP 401.
- `GET /api/v1/import-safety` — reports whether every hardlink safety prerequisite is satisfied.
- `POST /api/v1/analyses/{analysisId}/approve-import` — revalidates the complete plan and returns a short-lived approval ID and bearer token.
- `POST /api/v1/approvals/{approvalId}/status` with `{"token":"..."}` — retrieves durable execution state, including terminal success after a lost reconciliation response.
- `POST /api/v1/approvals/{approvalId}/execute` with `{"token":"..."}` — consumes the approval, publishes immutable native authorizations, and submits exactly one Lidarr command.
- `POST /api/v1/approvals/{approvalId}/reconcile` with `{"token":"..."}` — returns HTTP 202 while the command runs and reports success only after complete per-file reconciliation.

The companion release archive includes `ai-importctl.py`, a dependency-free client for these routes. Set `AIIMPORT_URL` and `COMPANION_API_TOKEN`, then use `safety`, `list`, `approve <analysis-id>`, `execute <approval-id> --token-file <0600-file>`, `status <approval-id> --token-file <0600-file>`, and `reconcile --wait <approval-id> --token-file <0600-file>`. Tokens may instead come from protected stdin or `AIIMPORT_APPROVAL_TOKEN`; they are never positional command arguments.

Discovery refuses versions other than `3.1.2.4913`, verifies Lidarr ownership relationships, excludes tracks that already have files, rejects duplicate source identities, and maps each source one-to-one. An LLM proposal never authorizes a write. Keep the service on a private network even though routes require authentication, because responses contain library metadata and approval tokens authorize imports.

### First import and rollback

1. Back up Lidarr's `/config` directory and database.
2. Choose one expendable album whose source torrent can be rechecked.
3. Confirm `/api/v1/import-safety` returns `executionEnabled: true` with no blockers.
4. Approve, execute, and call reconcile until it returns `succeeded`. Do not repeat `execute` after a timeout or connection loss.
5. Confirm the source and library files have the same device and inode (`stat -c '%d %i %n' ...`).
6. Recheck the torrent before importing more music. This validation is strongly recommended even when you choose to trial the RC directly.

If reconciliation is indeterminate, stop. Do not issue a second approval for the same files. Restore the Lidarr backup only after preserving `/data/approvals.json` and `/data/authorizations` for diagnosis; deleting those records does not safely undo a command that may already have run.

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

The script needs Docker, runs both test projects, produces the plugin and companion v1.0 RC archives under `artifacts/`, and publishes the unpacked companion under `artifacts/companion/`. It pins Lidarr source to the target image's plugin commit.

## Roadmap

- **Phase 1:** feasibility, loadable plugin, settings, Test Connection — implemented.
- **Phase 2:** read-only failed-import discovery, deterministic candidate generation, Lidarr/MusicBrainz-ID verification, dry-run proposals — merged.
- **Phase 3:** descriptor-bound hardlink execution, durable approvals, helper attestation, and fail-closed per-file reconciliation — implemented in the v1.0 release candidate.
- **Phase 4:** batch UX and opt-in automation after sufficient validation evidence.

## License

GPL-3.0-only, matching Lidarr's license because this plugin links against Lidarr internals.
