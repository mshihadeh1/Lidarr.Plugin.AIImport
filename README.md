# Lidarr AI Import v1.1.5

Standalone, approval-gated AI-assisted import matching for **Lidarr 3.1.2.4913**.

The plugin runs inside Lidarr. It does not use a companion service, a companion URL, or separate Lidarr credentials. It discovers failed imports, constrains matching to current Lidarr metadata, requires explicit approval, invokes Lidarr's native manual-import pipeline, and permits only verified hardlinks.

## Safety model

- Exact Lidarr compatibility: `3.1.2.4913` only.
- The LLM may rank only candidates supplied by Lidarr; returned IDs and relationships are revalidated. Confidence is advisory and never authorization.
- A user may construct a complete mapping only from current server-supplied Lidarr releases and tracks. The server independently validates exhaustiveness, uniqueness, ownership, source freshness, and destination constraints before creating a new reviewable proposal.
- Adding a missing artist/album is a separate explicit action, uses exact MusicBrainz IDs read from every source file, and is reconciled through Lidarr's own metadata services before file import is possible.
- Discovery is read-only.
- Approval is expiring, single-use, and bound to the current source/Lidarr state.
- Execution revalidates the plan immediately before submitting it to Lidarr.
- The packaged native helper is SHA-256 verified, copied into a sealed anonymous `memfd`, and executed through `/proc/self/fd/4095`.
- Native authorization binds the source device/inode/metadata, destination root, Lidarr IDs, and expiry.
- Copy fallback is forbidden. Cross-device imports fail rather than copying.
- If post-link validation fails, the helper fails and retains the created hardlink for inspection rather than risk deleting a concurrently replaced file. Do not retry automatically.
- Success requires native-transfer, Lidarr command, history, track-file, and track-binding reconciliation.
- Workflow state is stored beneath `<Lidarr app data>/ai-import`.

## Requirements

- Linux x86-64.
- Lidarr `3.1.2.4913` from the plugin-capable branch/image.
- Download and library paths visible to Lidarr on the **same filesystem**.
- An OpenAI-compatible chat-completions endpoint.
- A saved `AI Import` Connect provider in Lidarr.

Before execution, these Lidarr Media Management settings must be exact:

- **Copy Using Hardlinks:** enabled
- **Use Script Import:** enabled
- **Script Import Path:** `/proc/self/fd/4095`
- **Set Permissions:** disabled
- **File Date:** `None`
- **Import Extra Files:** disabled
- **Write Audio Tags:** `No`
- **Scrub Audio Tags:** disabled
- **Embed Cover Art:** disabled

The plugin checks these settings again before authorization and command submission. A mismatch fails closed.

## Installation

### From a GitHub release

Install `Lidarr.Plugin.AIImport-v1.1.5.net8.0.zip` through Lidarr's plugin support, then restart Lidarr.

### Manual installation

1. Extract these four files into:

   ```text
   <Lidarr app data>/plugins/mshihadeh1/Lidarr.Plugin.AIImport/
   ```

   - `Lidarr.Plugin.AIImport.dll`
   - `lidarr-hardlink-import`
   - `LICENSE`
   - `SOURCE.txt`

2. Set the helper executable:

   ```bash
   chmod 0555 <plugin-directory>/lidarr-hardlink-import
   ```

3. Restart Lidarr.
4. Confirm **System → Plugins** reports `AI Import` version `1.1.5.0`.
5. Configure the exact Media Management settings listed above and restart Lidarr once more.

## Configure the LLM endpoint

In **Settings → Connect**, add and save **AI Import**. Configure:

- API base URL, such as `https://api.openai.com/v1`
- API key
- Model
- Temperature
- Request timeout
- Maximum output tokens
- Optional custom system prompt
- **Disable Model Thinking** for compatible Qwen/llama.cpp endpoints

- **Untracked Download Folders** (optional): absolute folders scanned for albums downloaded outside Lidarr; separate multiple folders with a pipe, comma, or newline. The 25 newest album folders are scanned per discovery. Each root must live on the same filesystem as the destination library root for hardlink import.

Use **Test** before saving. Endpoint credentials remain in Lidarr's provider settings and are not included in workflow state, prompts, action responses, or logs.

## Using the plugin in Lidarr

1. Open **Settings → Connect → AI Import**.
2. Press **Open AI Import Workspace**. Lidarr opens an authenticated, same-origin workspace; no shell or separate Lidarr API credentials are needed.
3. Press **Discover failed imports**. Discovery is read-only. It examines queue entries in `importBlocked` or `importFailed`, and—if "Untracked Download Folders" is configured—it also scans those folders for album folders downloaded outside Lidarr (for example straight into the qBittorrent download path, including files renamed by qBittorrent). Each untracked scan appears as its own reviewable analysis with the source folder shown on the card.
4. Review each source-to-track mapping. If a low-confidence or `manualReview` result has a current verified Lidarr release candidate, choose exactly one target track for every source and press **Create manually reviewed proposal**. This cannot introduce arbitrary IDs or paths and does not approve or import anything.
5. For a complete `proposed` result—including a manually constructed proposal—check the review acknowledgement and press **Approve reviewed mapping**. Approval does not link files and expires after 15 minutes.
6. Review the approved plan again, type `IMPORT`, and press **Execute hardlink import**. This is a distinct, single-use action.
7. Use **Status** and **Reconcile** to verify the result. Success is not established by command completion alone.

The workspace is served by the plugin through Lidarr's authenticated UI resource pipeline. Lidarr `3.1.2.4913` has no public frontend-plugin API, so this integration is intentionally pinned to that exact version.

Upgrading to v1.1.0 intentionally invalidates analyses created by earlier plugin builds. Restart Lidarr and run **Discover** again before approving anything. Existing executing or indeterminate approvals remain status/reconciliation records; do not retry an uncertain import.

v1.1.1 excludes both Lidarr's null-identity existing-library decisions and source rows proven already imported. Suppression requires one matching import-history event, matching current track-file and track bindings, matching quality/size/flags, and identical source/destination device and inode. Contradictory evidence is excluded from approval and shown as requiring reconciliation. The plugin does not delete or clear Lidarr Activity entries; stale rows may remain visible in Lidarr while the plugin ignores them. For an explicitly human-reviewed proposal, Lidarr must still accept the exact source, artist, album, and release without rejections, but its automatic track guess no longer overrides the user's separately validated track selection. Run **Discover** again after upgrading; do not reuse an older analysis.

v1.1.2 accepts Lidarr's authoritative review when it returns each approved source exactly once plus unrelated existing-library rows with no download identity. Missing or duplicate approved sources and unrelated rows carrying a download identity still fail closed. Restart Lidarr and run **Discover** again after upgrading.

v1.1.3 narrows that exception to rows Lidarr explicitly marks as additional existing-library files, requires absolute unique extra paths, and revalidates the approved row's ID, path, download identity, size, quality, flags, and non-additional status after Lidarr review.

v1.1.4 lets a provenance-bound complete human mapping override only Lidarr's automatic tag-distance and track-completeness rejections. Import-safety and library-state rejections—including free space, existing imports, upgrades, root folders, and unpacking—still fail closed. Approval errors now identify whether source material, ownership, safety rejection, or automatic mapping changed.

v1.1.5 uses the same reconciled manual-import scan for discovery, human mapping creation, library addition, and approval. Stale Activity rows proven already imported no longer make a newly created human mapping immediately stale; contradictory evidence still fails closed.

v1.2.0 adds **Untracked Download Folders** to the AI Import connect settings. Configured absolute folders are scanned during discovery for albums downloaded outside Lidarr's queue—e.g. files landing directly in the qBittorrent download path (including names renamed by qBittorrent). An untracked folder is eligible when it contains no existing Lidarr track files, its files' tags resolve to at most one Lidarr album, and Lidarr's manual-import review (`/api/v3/import/manual/import`) already assigns the file set to that album. Discovery then reuses the existing verified-candidate pipeline (deterministic first, LLM ranking optional, manual mapping, expiring approval, single-use execution, and reconciliation)—no artist/album add and no manual import are required beforehand. Approval and reconciliation scope untracked sources by folder and album instead of download ID, and the execution payload submits an empty download identity for those files.

### Downloads for artists not yet in Lidarr

When qBittorrent sends a failed/blocked download to Lidarr and every source file has the same valid MusicBrainz artist, release-group, and release tags, discovery offers **Add verified artist and album**. Select an existing accessible Lidarr root folder, quality profile, and metadata profile, review the exact MusicBrainz IDs, and explicitly approve the library addition. The plugin calls Lidarr's own metadata, add, refresh, release-monitoring, and read-back services. It adds or reuses only the exact artist/album, selects only the tagged release, disables searches, and does not import files during this action. Run discovery again, review the resulting Lidarr-backed mapping, create an expiring approval, and execute normally.

Lidarr's add services are not transactional. If an add or refresh fails after a write, the workspace reports a **partial** result rather than claiming that nothing changed. Run **Discover** to reconcile current Lidarr state before retrying. Existing artists must use the selected root folder, quality profile, and metadata profile; mismatches are rejected before mutation.

If those MusicBrainz tags are missing, conflicting, or invalid, the plugin fails closed instead of guessing an artist or release. Tagging a torrent only with the qBittorrent/Lidarr category is enough to route it to Lidarr, but the audio files still need authoritative MusicBrainz artist, release-group, and release identities before an absent artist can be added safely.

If an execute response is lost or reports `Indeterminate`, do not execute again. Use **Status** and **Reconcile**. `Indeterminate` is never permission to retry automatically.

## Operational guidance

1. Try one non-critical failed album first.
2. Verify the source remains available to the torrent client.
3. Confirm source and destination share device and inode:

   ```bash
   stat -c '%d:%i %n' /path/to/source.flac /path/to/library/file.flac
   ```

4. Recheck the torrent before increasing usage.
5. Keep backups. The plugin is deliberately conservative but does not replace normal backup practices.

## Building

```bash
git submodule update --init --recursive
VERSION=1.1.5 ./scripts/build.sh
```

A dirty local verification build requires `ALLOW_DIRTY_BUILD=1` and intentionally does not claim a corresponding-source commit URL. Release packages must be produced from a clean tagged tree.

The release ZIP contains only the plugin DLL, native helper, GPL-3.0 license, and source notice.

## License

GPL-3.0-only. See `LICENSE` and `SECURITY.md`.
