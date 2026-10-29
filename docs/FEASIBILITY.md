# Feasibility report

Research target: `ghcr.io/linuxserver-labs/prarr:lidarr-plugins`, package `3.1.2.4913`, built from Lidarr plugin branch commit [`e42a7ca`](https://github.com/Lidarr/Lidarr/commit/e42a7ca4fd633e021d69da7daa0368b870b0282e).

## Verified plugin model

### Installation and loading

- Lidarr installs plugins from GitHub releases. `PluginService` looks for a release asset containing `net{runtime-major}.0.zip`, filters optional `Minimum Lidarr Version` release text, and supports repository/tree URLs: [`PluginService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/Plugins/PluginService.cs).
- `InstallPluginService` extracts into the app-data plugin directory as `<owner>/<repository>`: [`InstallPluginService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/Plugins/InstallPluginService.cs).
- Startup scans exactly `Lidarr.Plugin.*.dll` below the two-level owner/repository hierarchy and loads each assembly in `PluginLoadContext`: [`PathExtensions.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Common/Extensions/PathExtensions.cs), [`PluginLoader.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Common/Composition/PluginLoader.cs).
- Plugin identity is a class derived from `NzbDrone.Core.Plugins.Plugin`, implementing `Name`, `Owner`, and `GithubUrl`: [`Plugin.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/Plugins/Plugin.cs).

### Working-plugin pattern

Tubifarry confirms the practical pattern:

- .NET 8 plugin assembly named `Lidarr.Plugin.*`.
- Compile-time project references to Lidarr Common/Core/HTTP with runtime assets excluded.
- Settings are exposed by implementing an existing provider type such as `NotificationBase<TSettings>` or metadata/indexer/download-client abstractions.
- Settings use `IProviderConfig` and `FieldDefinition`; providers implement `Test()` for Lidarr's built-in Test button.

Sources: [Tubifarry repository](https://github.com/TypNull/Tubifarry), [project file](https://github.com/TypNull/Tubifarry/blob/master/Tubifarry/Tubifarry.csproj), and [QueueCleaner provider](https://github.com/TypNull/Tubifarry/tree/master/Tubifarry/Notifications/QueueCleaner).

## Manual-import integration points

### Candidate discovery

- `GET /api/v1/manualimport?folder=...` or `?downloadId=...` calls `IManualImportService.GetMediaFiles(...)`.
- Candidate resources already contain source path/name/size, parsed artist/album/release/tracks, quality, embedded `AudioTags`, `DownloadId`, and import `Rejections`.
- In process, the corresponding interface is `NzbDrone.Core.MediaFiles.TrackImport.Manual.IManualImportService`.

Sources: [`ManualImportController.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/Lidarr.Api.V1/ManualImport/ManualImportController.cs), [`ManualImportService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/MediaFiles/TrackImport/Manual/ManualImportService.cs), [`ManualImportResource.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/Lidarr.Api.V1/ManualImport/ManualImportResource.cs).

### Reprocessing and execution

1. The UI posts selected candidate overrides to `POST /api/v1/manualimport`; this re-runs Lidarr's decision engine and returns updated rejections. It does not execute the transfer.
2. Approved mappings are executed through `POST /api/v1/command` with command name `ManualImport`, `files`, `importMode`, and `replaceExistingFiles`.
3. Internally this is `ManualImportCommand`, handled by `ManualImportService.Execute`, which calls `IImportApprovedTracks.Import` and therefore preserves Lidarr's database/history/event pipeline.

Sources: [`interactiveImportActions.js`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/frontend/src/Store/Actions/interactiveImportActions.js), [`InteractiveImportModalContentConnector.js`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/frontend/src/InteractiveImport/Interactive/InteractiveImportModalContentConnector.js), [`ManualImportCommand.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/MediaFiles/TrackImport/Manual/ManualImportCommand.cs).

**Verdict:** discovery, dry-run/reprocessing, and native manual-import execution are feasible without direct database writes.

## UI feasibility

The plugin loader makes backend types available to Lidarr's composition container, so existing provider screens can expose configuration and Test Connection. However, MVC startup explicitly registers only Lidarr's API and static-resource assemblies as application parts; there is no verified plugin frontend-bundle or navigation-extension contract.

Source: [`Startup.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Host/Startup.cs).

**Verdict:** a custom “AI Match” button/page inside the manual-import grid is not supported by a verified native plugin API. Do not patch core frontend in the initial implementation.

Smallest supported UX:

- Plugin configuration and Test button under Lidarr **Settings → Connect**.
- Authenticated `AiImportConnector.RequestAction` calls for selection, proposal review, approval, execution, status, and reconciliation, using only direct in-process Lidarr services.
- If a future plugin SDK adds frontend slots, move that UX into Lidarr without changing the matching core.

## Hardlink feasibility and controlled proof

Lidarr's `ImportMode` values are `Auto`, `Move`, and `Copy`; there is no hardlink-only mode: [`ImportMode.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/MediaFiles/TrackImport/ImportMode.cs).

When Copy Using Hardlinks is enabled, `TrackFileMovingService.CopyTrackFile` requests `TransferMode.HardLinkOrCopy`: [`TrackFileMovingService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/MediaFiles/TrackFileMovingService.cs). `DiskTransferService` tries a hardlink, then copies if the hardlink fails because the mode also contains `Copy`: [`DiskTransferService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Common/Disk/DiskTransferService.cs).

Consequences:

- Setting `ImportMode=Copy` and enabling hardlinks does not guarantee a hardlink.
- Docker paths that look related can still be different mounts/devices.
- Native Lidarr does not expose the actual transfer mode in the manual-import command result.

The built-in import-script hook runs before `DiskTransferService`. Exit code `0` tells Lidarr the transfer completed, exit code `3` defers to Lidarr's unsafe `HardLinkOrCopy`, and every other code fails that file. The packaged script never returns `3` for a requested `HardLinkOrCopy`: it creates a hardlink with `ln`, verifies device/inode equality, and otherwise fails.

This was exercised through Lidarr's real `ManualImport` command in a disposable 3.1.2.4913 container. Same-filesystem import preserved source hash, size, mtime, mode, UID/GID, and device/inode while creating native track-file/history records. A cross-device import exited `24`, created no destination, and created no track-file record.

Two additional constraints were discovered:

- Lidarr's `StringDictionary` lowercases script environment keys on Linux; the script must read `lidarr_transfermode`, `lidarr_sourcepath`, and `lidarr_destinationpath`.
- A `ManualImport` command can still report `completed` and “Manually imported 1 files” after the individual script import failed. Command status alone is unsafe evidence.

**Safety decision:** approval/import routes remain disabled. The current Phase 3 foundation:

1. Require `CopyUsingHardlinks=true`, `replaceExistingFiles=false`, and `ImportMode=Copy`.
2. Requires Lidarr script import to point exactly at the packaged script.
3. Requires tag writing/scrubbing, embedded artwork, file-date changes, permission changes, and extra-file imports to be disabled.
4. Exposes these checks through authenticated read-only `GET /api/v1/import-safety`, with `executionEnabled=false`.
5. Packages and tests the no-copy script, including same-device success, cross-device failure, collision refusal, and argument/environment validation.

The script proves that Script Import can prevent the ordinary copy fallback while retaining Lidarr's native database/history pipeline. It does not prove a race-free security boundary: independent review identified pathname TOCTOU and failure-cleanup races that a POSIX shell implementation cannot eliminate. A finished approval flow therefore requires a descriptor-safe native helper or pinned Lidarr implementation, plus fresh proposal/configuration validation, exact post-import reconciliation, idempotency, and explicit handling of Lidarr's false-positive command completion. A native upstream `HardLink` mode remains the preferred long-term architecture.

## Compatibility limits

- The available plugin mechanism is branch/version specific and references Lidarr internals, not a stable NuGet SDK.
- This repository pins the exact target commit.
- A release for another Lidarr version requires rebuilding and running the disposable-container load test.
- No claim is made for stock stable/nightly images that do not expose **System → Plugins**.
