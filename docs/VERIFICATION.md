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
