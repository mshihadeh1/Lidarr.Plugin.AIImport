# Security policy

## Reporting

Please report vulnerabilities privately through GitHub Security Advisories rather than a public issue.

## Secret handling

- API keys are configuration only and must never enter prompts or logs.
- Tests and examples use placeholders only.
- Match requests must contain only metadata required to rank verified candidates.
- Base URLs containing embedded credentials, queries, or fragments are rejected, endpoint responses are capped at 1 MiB, and Test Connection errors redact the configured API key.

## Dependency boundary

The release ZIP contains only this plugin DLL, the statically linked strict-hardlink helper, its GPL license, and a corresponding-source notice. Lidarr host dependencies are compile-time project references and are not redistributed. CI reports inherited vulnerable packages from the pinned Lidarr source; remediation requires a separately verified Lidarr compatibility pin rather than silently replacing host assemblies.

## Import safety

The v1 release candidate permits only explicit, authenticated, single-use approvals. It publishes owner-only immutable authorization records, invokes Lidarr's native manual-import pipeline, and intercepts `HardLinkOrCopy` with a descriptor-bound helper that either creates the authorized hardlink or fails without copy fallback. Destinations must remain beneath the approved artist root.

Execution is not successful until the native result, source/destination device and inode, Lidarr history, enriched track-file state, and track binding all reconcile. Unknown outcomes are never retried automatically. Approval tokens expire for execution but remain recovery credentials for status and reconciliation; protect them in owner-only files or protected stdin.

The exact supported host is Lidarr `3.1.2.4913`. Back up Lidarr, keep the service private, and complete a disposable one-album import plus torrent-client recheck before broader use.
