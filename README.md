# Lidarr AI Import v1.0.1

Standalone, approval-gated AI-assisted import matching for **Lidarr 3.1.2.4913**.

The plugin runs inside Lidarr. It does not use a companion service, a companion URL, or separate Lidarr credentials. It discovers failed imports, constrains matching to current Lidarr metadata, requires explicit approval, invokes Lidarr's native manual-import pipeline, and permits only verified hardlinks.

## Safety model

- Exact Lidarr compatibility: `3.1.2.4913` only.
- The LLM may rank only candidates supplied by Lidarr; returned IDs and relationships are revalidated.
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

Install `Lidarr.Plugin.AIImport-v1.0.1.net8.0.zip` through Lidarr's plugin support, then restart Lidarr.

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
4. Confirm **System → Plugins** reports `AI Import` version `1.0.1.0`.
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

Use **Test** before saving. Endpoint credentials remain in Lidarr's provider settings and are not included in workflow state, prompts, action responses, or logs.

## Using the plugin in Lidarr

1. Open **Settings → Connect → AI Import**.
2. Press **Open AI Import Workspace**. Lidarr opens an authenticated, same-origin workspace; no shell or separate Lidarr API credentials are needed.
3. Press **Discover failed imports**. Discovery is read-only and examines queue entries in `importBlocked` or `importFailed`.
4. Review each source-to-track mapping. `manualReview` results are read-only and cannot be approved.
5. For a `proposed` result, check the review acknowledgement and press **Approve reviewed mapping**. Approval does not link files and expires after 15 minutes.
6. Review the approved plan again, type `IMPORT`, and press **Execute hardlink import**. This is a distinct, single-use action.
7. Use **Status** and **Reconcile** to verify the result. Success is not established by command completion alone.

The workspace is served by the plugin through Lidarr's authenticated UI resource pipeline. Lidarr `3.1.2.4913` has no public frontend-plugin API, so this integration is intentionally pinned to that exact version.

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
VERSION=1.0.1 ./scripts/build.sh
```

A dirty local verification build requires `ALLOW_DIRTY_BUILD=1` and intentionally does not claim a corresponding-source commit URL. Release packages must be produced from a clean tagged tree.

The release ZIP contains only the plugin DLL, native helper, GPL-3.0 license, and source notice.

## License

GPL-3.0-only. See `LICENSE` and `SECURITY.md`.
