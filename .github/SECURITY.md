# Security Policy

## Supported Versions

Security fixes are handled for the current released CoreAI package versions and
the active `main` branch. Older versions may receive fixes when the issue is
practical to backport, but users should prefer upgrading to the latest release.

## Reporting a Vulnerability

Please do not open public GitHub issues for suspected vulnerabilities.

Report security issues privately through GitHub's **Report a vulnerability**
flow for this repository. Open the repository's **Security** tab and use the
private security advisory workflow instead of posting a public issue.

Include:

- affected CoreAI version or commit;
- Unity version and target platform;
- a minimal reproduction or proof of concept;
- impact summary and any known workaround.

The maintainer will review the report, confirm the impact where possible, and
coordinate a fix or disclosure timeline before public discussion.

Please avoid sharing exploit details publicly until a fix or mitigation is
available. If the report affects a dependency or upstream package, include that
context so the maintainer can coordinate responsibly.

## Lua Sandbox Notes

CoreAI includes a Lua sandbox for AI-written gameplay scripts. Review the
sandbox boundary, removed APIs, execution limits, and binding rules here:

[Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md](../Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md)
