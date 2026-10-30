# Lidarr AI Import v1.0

Standalone, approval-gated AI-assisted import matching for **Lidarr 3.1.2.4913**.

The plugin runs inside Lidarr. It does not use a companion service, a companion URL, or separate Lidarr credentials. It discovers failed imports, constrains matching to current Lidarr metadata, requires explicit approval, invokes Lidarr's native manual-import pipeline, and permits only verified hardlinks.

## Safety model

- Exact Lidarr compatibility: `3.1.2.4913` only.
- The LLM may rank only candidates supplied by Lidarr; returned IDs and relationships are revalidated.
- Discovery is read-only.
- Approval is expiring, single-use, and bound to the current source/Lidarr state.
- Execution revalidates the plan immediately before submitting it to Lidarr.
- The packaged native helper is SHA-256 verified, copied into a sealed anonymous `memfd`, and executed through `/proc/self/fd/198`.
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
- **Script Import Path:** `/proc/self/fd/198`
- **Set Permissions:** disabled
- **File Date:** `None`
- **Import Extra Files:** disabled
- **Write Audio Tags:** `No`
- **Scrub Audio Tags:** disabled
- **Embed Cover Art:** disabled

The plugin checks these settings again before authorization and command submission. A mismatch fails closed.

## Installation

### From a GitHub release

Install `Lidarr.Plugin.AIImport-v1.0.0.net8.0.zip` through Lidarr's plugin support, then restart Lidarr.

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
4. Confirm **System → Plugins** reports `AI Import` version `1.0.0.0`.
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

Use **Test** before saving. Endpoint credentials remain in Lidarr's provider settings and are not included in workflow state, prompts, action responses, or logs.

## Using the plugin

v1.0 exposes controls through Lidarr's authenticated notification-provider action API. It intentionally does not add a custom frontend bundle.

First fetch the saved provider definition:

```bash
LIDARR_URL='http://localhost:8686'
LIDARR_API_KEY='your-lidarr-api-key'

curl -fsS -H "X-Api-Key: $LIDARR_API_KEY" \
  "$LIDARR_URL/api/v1/notification" \
  | jq 'map(select(.implementation == "AiImportConnector"))[0]' \
  > /tmp/ai-import-provider.json
```

### 1. Discover

```bash
curl -fsS -X POST \
  -H "X-Api-Key: $LIDARR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data @/tmp/ai-import-provider.json \
  "$LIDARR_URL/api/v1/notification/action/discover"
```

Discovery examines queue entries in `importBlocked` or `importFailed`. Review each returned proposal and retain its `analysisId`.

### 2. Approve

```bash
ANALYSIS_ID='reviewed-analysis-id'
curl -fsS -X POST \
  -H "X-Api-Key: $LIDARR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data @/tmp/ai-import-provider.json \
  "$LIDARR_URL/api/v1/notification/action/approve?analysisId=$ANALYSIS_ID"
```

Approval does not move or link files. It returns an expiring `approvalId`.

### 3. Execute

```bash
APPROVAL_ID='approved-id'
curl -fsS -X POST \
  -H "X-Api-Key: $LIDARR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data @/tmp/ai-import-provider.json \
  "$LIDARR_URL/api/v1/notification/action/execute?approvalId=$APPROVAL_ID"
```

Execution is single-use. Do not retry an uncertain response with a new approval; inspect status and reconcile first.

### 4. Check status and reconcile

```bash
curl -fsS -X POST \
  -H "X-Api-Key: $LIDARR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data @/tmp/ai-import-provider.json \
  "$LIDARR_URL/api/v1/notification/action/status?approvalId=$APPROVAL_ID"

curl -fsS -X POST \
  -H "X-Api-Key: $LIDARR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data @/tmp/ai-import-provider.json \
  "$LIDARR_URL/api/v1/notification/action/reconcile?approvalId=$APPROVAL_ID"
```

A successful terminal state requires all native and Lidarr evidence to agree. `Indeterminate` means stop and inspect; it is not permission to retry automatically.

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
VERSION=1.0.0 ./scripts/build.sh
```

A dirty local verification build requires `ALLOW_DIRTY_BUILD=1` and intentionally does not claim a corresponding-source commit URL. Release packages must be produced from a clean tagged tree.

The release ZIP contains only the plugin DLL, native helper, GPL-3.0 license, and source notice.

## License

GPL-3.0-only. See `LICENSE` and `SECURITY.md`.
