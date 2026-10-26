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
- A narrowly scoped companion web UI/service for selection, proposal review, and approval, communicating only with Lidarr's public REST API.
- If a future plugin SDK adds frontend slots, move that UX into Lidarr without changing the matching core.

## Hardlink feasibility and blocker

Lidarr's `ImportMode` values are `Auto`, `Move`, and `Copy`; there is no hardlink-only mode: [`ImportMode.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/MediaFiles/TrackImport/ImportMode.cs).

When Copy Using Hardlinks is enabled, `TrackFileMovingService.CopyTrackFile` requests `TransferMode.HardLinkOrCopy`: [`TrackFileMovingService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Core/MediaFiles/TrackFileMovingService.cs). `DiskTransferService` tries a hardlink, then copies if the hardlink fails because the mode also contains `Copy`: [`DiskTransferService.cs`](https://github.com/Lidarr/Lidarr/blob/e42a7ca4fd633e021d69da7daa0368b870b0282e/src/NzbDrone.Common/Disk/DiskTransferService.cs).

Consequences:

- Setting `ImportMode=Copy` and enabling hardlinks does not guarantee a hardlink.
- Docker paths that look related can still be different mounts/devices.
- Native Lidarr does not expose the actual transfer mode in the manual-import command result.

**Safety decision:** this release performs no imports. Phase 3 must, before command submission:

1. Require `CopyUsingHardlinks=true`, `replaceExistingFiles=false`, and `ImportMode=Copy`.
2. Resolve the exact destination path using Lidarr naming rules.
3. Compare filesystem device IDs inside the Lidarr mount namespace.
4. Create and remove a temporary hardlink probe across the exact source/destination parent filesystems.
5. Refuse before import if any check fails.
6. After native import, compare source/destination device+inode and mark the operation failed if they differ.

A true hardlink-only guarantee requires either an upstream Lidarr `HardLink` import mode (preferred) or a narrowly maintained compatible build. The companion cannot replace Lidarr's file engine.

## Compatibility limits

- The available plugin mechanism is branch/version specific and references Lidarr internals, not a stable NuGet SDK.
- This repository pins the exact target commit.
- A release for another Lidarr version requires rebuilding and running the disposable-container load test.
- No claim is made for stock stable/nightly images that do not expose **System → Plugins**.
