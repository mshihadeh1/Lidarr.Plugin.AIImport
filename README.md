# Lidarr AI Import Plugin

A safety-first Lidarr plugin foundation for OpenAI-compatible, metadata-constrained import matching.

> **Current release status:** Phase 1 plus unintegrated matching primitives. It loads in the plugin-enabled Lidarr build, exposes LLM settings under **Settings → Connect → AI Import**, tests the configured chat-completions endpoint, and includes strict match-proposal parsing for future matching work. It does **not** discover failed imports or import, move, copy, rename, retag, or delete music.

## Why the initial release is read-only

The investigated Lidarr build can enumerate and execute manual imports, but its native `Copy` path uses `HardLinkOrCopy`: when hardlink creation fails it silently falls back to a copy. That cannot satisfy this project's hardlink-only requirement. Import execution stays disabled until a preflight and post-import verification design can guarantee device/inode preservation.

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

Extract the release DLL under the persistent `/config/plugins/<owner>/<repository>/` hierarchy:

```text
/config/plugins/mshihadeh1/Lidarr.Plugin.AIImport/Lidarr.Plugin.AIImport.dll
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

The script needs Docker and produces `artifacts/Lidarr.Plugin.AIImport-v0.1.0.net8.0.zip`. It pins Lidarr source to the target image's plugin commit.

## Roadmap

- **Phase 1:** feasibility, loadable plugin, settings, Test Connection — implemented.
- **Phase 2:** read-only failed-import discovery, deterministic candidate generation, MusicBrainz/Lidarr verification, dry-run proposals.
- **Phase 3:** manual approval and native import only after hardlink-only safety can be enforced and verified.
- **Phase 4:** batch UX and opt-in automation after sufficient validation evidence.

## License

GPL-3.0-only, matching Lidarr's license because this plugin links against Lidarr internals.
