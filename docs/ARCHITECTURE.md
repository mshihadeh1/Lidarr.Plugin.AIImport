# Architecture

## Trust boundaries

1. **Lidarr and MusicBrainz are authoritative.** The LLM may rank only supplied candidates.
2. **The LLM output is untrusted.** Parse strict JSON, reject unknown fields, then verify every ID and relationship against current metadata.
3. **Filesystem operations belong to Lidarr.** The project will not implement an independent mover/copier/tagger.
4. **Imports are approval-gated.** Automatic import remains out of scope until manual mode has operational evidence.
5. **Companion metadata is authenticated.** Every `/api` request requires the independently configured `X-Companion-Api-Key`; `/health` contains no library metadata and remains unauthenticated.

## Components

- `Configuration/AiImportSettings`: Lidarr-native provider settings and validation.
- `Configuration/AiImportConnector`: existing Connect-provider UI and Test Connection integration.
- `Llm/IOpenAiChatClient`: provider-neutral OpenAI chat-completions abstraction.
- `Matching/StructuredMatchParser`: current strict runtime enforcement layer, including fenced-JSON fallback for models without native JSON-schema support. The published schema documents representable constraints but cannot detect duplicate object keys or uniqueness by only `sourceFileId`.
- Companion `Discovery`: paginated `importBlocked`/`importFailed` queue items and public manual-import API candidates.
- Companion `Metadata`: release and track ownership retrieved from Lidarr; embedded MusicBrainz track IDs are accepted only when they match a returned Lidarr track.
- Companion `Matching`: maximum-cardinality, maximum-score one-to-one deterministic assignment; existing library tracks are excluded. Ambiguous results remain manual review in Phase 2.
- Companion `ImportSafety`: authenticated read-only assessment of the exact script path and every Lidarr setting that could copy, retag, chmod, timestamp, embed artwork, or import extras. This assessment does not enable execution.
- Packaged `lidarr-hardlink-import`: demonstrates interception of `HardLinkOrCopy`, invokes `ln` without a copy path, verifies source/destination device+inode identity, and returns a hard failure rather than Lidarr's defer code when linking fails. It remains a tested feasibility primitive, not an execution-ready security boundary, because shell pathname operations cannot eliminate TOCTOU races.
- Future companion UI: selection/review/approval only, using Lidarr's REST API.

## Matching pipeline

1. Read failed-import candidates from Lidarr.
2. Normalize tags and filenames deterministically.
3. Prefer embedded MusicBrainz track IDs only when they match a track returned by Lidarr.
4. Query only Lidarr's configured public REST API in Phase 2.
5. Exclude existing library tracks and require every source file to receive a unique mapping based on IDs, disc/track numbers, titles, and durations.
6. If one candidate is unambiguous, propose it without an LLM.
7. In Phase 2, return ambiguous matches for manual review. A future LLM ranker may receive only minimal candidate metadata with opaque file IDs.
8. Strictly parse any future model response.
9. Verify every returned ID is positive and in the supplied candidate set, source IDs are unique, album/release ownership is explicit, and tracks come from the release-scoped Lidarr endpoint with matching album ownership. Apply these invariants to deterministic and future model proposals alike.
10. Return a dry-run proposal or manual review; never invent a release.

`proposed` is a metadata-mapping result, not an assertion that Lidarr can import the file. Current Lidarr rejection reasons are preserved with each source file and cannot lead to execution in Phase 2, which exposes no approval or import route.

## Import state machine (future)

`Discovered → Proposed → Validated → Approved → HardlinkPreflightPassed → NativeImportQueued → NativeImportSucceeded → HardlinkVerified`

Every transition is append-only and idempotent by a digest of download ID, source file identity, selected release, mappings, and settings version. Any validation change invalidates approval.

Lidarr 3.1.2.4913 can report the `ManualImport` command as completed even when an individual import script fails. Command status is therefore not proof of import success. Future execution must reconcile the expected track-file record and source/destination identity; it must never retry an indeterminate command automatically.

## Data minimization

Allowed prompt fields: normalized artist/album/track text, durations, disc/track numbers, tag IDs, verified candidate metadata, rejection summaries, and opaque per-file IDs.

Forbidden prompt fields: API keys, Lidarr/torrent credentials, complete absolute paths, unrelated filenames, Docker details, environment variables, logs, or database contents.
