# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| Latest  | :white_check_mark: |
| Older   | :x:                |

Only the latest release of RayTree receives security fixes. Please upgrade to the latest version before reporting a vulnerability.

## Reporting a Vulnerability

If you discover a security vulnerability in RayTree, please:

1. **Do not** open a public GitHub issue.
2. Open a [GitHub Security Advisory](https://github.com/bitc0der/RayTree/security/advisories/new) with a description of the vulnerability, steps to reproduce, and potential impact.
3. Allow reasonable time for a fix to be developed and released before any public disclosure.

You will receive a response acknowledging the report, followed by updates as the issue is investigated and resolved.

## Scope

The following are out-of-scope:

- Vulnerabilities in third-party libraries (report to the upstream project).
- Denial-of-service via resource exhaustion (polling interval, batch size) — these are configurable by the consumer and not a library defect.
