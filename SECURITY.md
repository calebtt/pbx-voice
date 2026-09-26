# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability**. Don't open a public issue for a vulnerability.

Include what an attacker could do, the steps to reproduce it, and the version (`pbx-voice version`). Never include real phone numbers, SIP passwords, API keys, or call recordings; use placeholders.

## Supported versions

Fixes go into the latest release.

## Scope

In scope: the daemon, its MCP and control interfaces, the Grok plugin in this repository, and the release builds. How pbx-voice is meant to be deployed, and which controls hold on which setup, is described in [docs/security.md](docs/security.md).

Out of scope: the PBX, the SIP provider, the xAI API, and the agent host (report those to their vendors), and anything that needs the operator's own account or files, since the operator is trusted with everything.
