# Architecture

## Trust boundaries

1. **Lidarr and MusicBrainz are authoritative.** The LLM may rank only supplied candidates.
2. **The LLM output is untrusted.** Parse strict JSON, reject unknown fields, then verify every ID and relationship against current metadata.
3. **Filesystem operations belong to Lidarr.** The project will not implement an independent mover/copier/tagger.
4. **Imports are approval-gated.** Automatic import remains out of scope until manual mode has operational evidence.

## Components

- `Configuration/AiImportSettings`: Lidarr-native provider settings and validation.
- `Configuration/AiImportConnector`: existing Connect-provider UI and Test Connection integration.
- `Llm/IOpenAiChatClient`: provider-neutral OpenAI chat-completions abstraction.
- `Matching/StructuredMatchParser`: current strict runtime enforcement layer, including fenced-JSON fallback for models without native JSON-schema support. The published schema documents representable constraints but cannot detect duplicate object keys or uniqueness by only `sourceFileId`.
- Future `Discovery`: failed queue items and `IManualImportService` candidates.
- Future `Metadata`: deterministic MusicBrainz/Lidarr candidate retrieval and duplicate checks.
- Future `Matching`: deterministic scorer first; LLM receives only ambiguous verified candidates.
- Future `ImportSafety`: hardlink preflight and inode/device post-verification.
- Future companion UI: selection/review/approval only, using Lidarr's REST API.

## Matching pipeline

1. Read failed-import candidates from Lidarr.
2. Normalize tags and filenames deterministically.
3. Prefer embedded MusicBrainz IDs after validating them.
4. Query only configured Lidarr/LMD/self-hosted MusicBrainz endpoints.
5. Remove candidates incompatible with track counts, durations, disc/track numbers, existing library files, and rejection reasons.
6. If one candidate is unambiguous, propose it without an LLM.
7. Otherwise send minimal candidate metadata with opaque file IDs to the LLM.
8. Strictly parse the response.
9. Verify every returned ID is in the supplied candidate set and that track IDs belong to the selected release.
10. Return a dry-run proposal or manual review; never invent a release.

## Import state machine (future)

`Discovered → Proposed → Validated → Approved → HardlinkPreflightPassed → NativeImportQueued → NativeImportSucceeded → HardlinkVerified`

Every transition is append-only and idempotent by a digest of download ID, source file identity, selected release, mappings, and settings version. Any validation change invalidates approval.

## Data minimization

Allowed prompt fields: normalized artist/album/track text, durations, disc/track numbers, tag IDs, verified candidate metadata, rejection summaries, and opaque per-file IDs.

Forbidden prompt fields: API keys, Lidarr/torrent credentials, complete absolute paths, unrelated filenames, Docker details, environment variables, logs, or database contents.
