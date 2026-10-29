# Architecture

## Trust boundaries

1. **Lidarr and MusicBrainz are authoritative.** The LLM may rank only supplied candidates.
2. **The LLM output is untrusted.** Parse strict JSON, reject unknown fields, then verify every ID and relationship against current metadata.
3. **Filesystem operations belong to Lidarr.** The project will not implement an independent mover/copier/tagger.
4. **Imports are approval-gated.** Automatic import remains out of scope until manual mode has operational evidence.
5. **The plugin is standalone.** It uses direct in-process Lidarr services and the authenticated `AiImportConnector.RequestAction` provider route. It has no companion URL, self-HTTP, or separate Lidarr API credentials.

## Components

- `Configuration/AiImportSettings`: Lidarr-native provider settings and validation.
- `Configuration/AiImportConnector`: existing Connect-provider UI and Test Connection integration.
- `Llm/IOpenAiChatClient`: provider-neutral OpenAI chat-completions abstraction.
- `Matching/StructuredMatchParser`: current strict runtime enforcement layer, including fenced-JSON fallback for models without native JSON-schema support. The published schema documents representable constraints but cannot detect duplicate object keys or uniqueness by only `sourceFileId`.
- Plugin `Discovery`: `importBlocked`/`importFailed` tracked downloads and direct `IManualImportService` candidates.
- Companion `Metadata`: release and track ownership retrieved from Lidarr; embedded MusicBrainz track IDs are accepted only when they match a returned Lidarr track.
- Companion `Matching`: maximum-cardinality, maximum-score one-to-one deterministic assignment; existing library tracks are excluded. Ambiguous results remain manual review in Phase 2.
- Companion `ImportSafety`: authenticated assessment of the exact script path and every Lidarr setting that could copy, retag, chmod, timestamp, embed artwork, import extras, or persist release-selection changes. Execution remains blocked until every prerequisite passes and deployment sharing is explicitly confirmed.
- Packaged `lidarr-hardlink-import`: native Linux helper for immutable authorization publication, descriptor-bound hardlink creation, destination-root confinement, durable claims/results, and post-link identity verification. It never defers a missing `HardLinkOrCopy` context to Lidarr's copy-capable transfer path.
- Standalone approval/execution/reconciliation: durable single-use approvals below Lidarr app data, native authorization bindings, one Lidarr command receipt, and exact per-file fail-closed reconciliation.
- Control actions are `discover`, `approve`, `execute`, `reconcile`, and `status`; imports never run directly from discovery.

## Native helper trust gate

The helper attestor requires a root-owned, single-link, non-writable regular file and executes the already-open descriptor. Ordinary same-UID plugin extraction followed by mode `0555` does **not** satisfy this trust boundary, so execution fails closed. A sealed `memfd_create` installation with an embedded build hash and exact `/proc/self/fd/<fd>` Script Import path is the preferred standalone deployment design and remains a release blocker until container-verified.

## Matching pipeline

1. Read failed-import candidates from Lidarr.
2. Normalize tags and filenames deterministically.
3. Prefer embedded MusicBrainz track IDs only when they match a track returned by Lidarr.
4. Query only Lidarr's configured public REST API in Phase 2.
5. Exclude existing library tracks and require every source file to receive a unique mapping based on IDs, disc/track numbers, titles, and durations.
6. If one candidate is unambiguous, propose it without an LLM.
7. Return ambiguous matches for manual review or send only minimal candidate metadata with opaque file IDs to the configured ranker.
8. Strictly parse every model response.
9. Verify every returned ID is positive and in the supplied candidate set, source IDs are unique, album/release ownership is explicit, and tracks come from the release-scoped Lidarr endpoint with matching album ownership. Apply these invariants to deterministic and future model proposals alike.
10. Return a proposal or manual review; never invent a release. A proposal still requires explicit approval and complete revalidation before execution.

`proposed` is a metadata-mapping result, not an assertion that Lidarr can import the file. Current Lidarr rejection reasons are preserved; approval reconstructs and revalidates the exact current plan.

## Import state machine

`Discovered → Proposed → Validated → Approved → Executing → NativeAuthorizationsPublished → CommandRecorded → ReconciledSucceeded`

Approvals bind the analysis ID, download ID, source identity, selected release and tracks, quality/indexer metadata, safety fingerprint, and artist destination root. Any validation change invalidates approval. Execution is single-use; indeterminate outcomes require reconciliation rather than retry.

Lidarr 3.1.2.4913 can report the `ManualImport` command as completed even when an individual import script fails. Command status is therefore not proof of import success. Reconciliation requires the durable native result, hardlink identity, history event, enriched album-scoped track-file record, and track binding; an indeterminate command is never retried automatically.

## Data minimization

Allowed prompt fields: normalized artist/album/track text, durations, disc/track numbers, tag IDs, verified candidate metadata, rejection summaries, and opaque per-file IDs.

Forbidden prompt fields: API keys, Lidarr/torrent credentials, complete absolute paths, unrelated filenames, Docker details, environment variables, logs, or database contents.
