# Security policy

## Reporting

Please report vulnerabilities privately through GitHub Security Advisories rather than a public issue.

## Secret handling

- API keys are configuration only and must never enter prompts or logs.
- Tests and examples use placeholders only.
- Match requests must contain only metadata required to rank verified candidates.
- Base URLs containing embedded credentials, queries, or fragments are rejected, endpoint responses are capped at 1 MiB, and Test Connection errors redact the configured API key.

## Dependency boundary

The release ZIP contains only this plugin DLL, its GPL license, and a corresponding-source notice. Lidarr host dependencies are compile-time project references and are not redistributed. CI reports inherited vulnerable packages from the pinned Lidarr source; remediation requires a separately verified Lidarr compatibility pin rather than silently replacing host assemblies.

## Import safety

The initial release cannot execute imports. This is intentional: the pinned Lidarr version's native hardlink path may fall back to copying. Any contribution enabling imports must demonstrate preflight refusal, native-import use, no replacement by default, and post-import device/inode verification.
