# AI assistance and verification record

Prepared 7 October 2026. No employer assignment PDF or interview transcript is included in this project.

## Development assistance

GitHub Copilot was used to help design and implement the application, tests, dashboard and documentation. Generated suggestions were compiled and tested; failures were investigated rather than recorded as success. The candidate must review this work and explain the design and boundaries independently.

## Runtime evidence

- SDK package: GitHub.Copilot.SDK 1.0.14.
- Local SDK authentication and model discovery succeeded.
- A tool-free generic JSON probe succeeded.
- Two full live greenfield attempts failed the restricted response contract and safe-stopped. This is recorded as an integration limitation.
- Deterministic mode is the default evaluator path. It uses explicit fixtures and real compilation/HTTP checks.
- No new paid provider API, extracted token, hidden provider proxy or automatic demo substitution is used.

## Engineering corrections during implementation

- NuGet audit identified an unsafe transitive SQLite native bundle; pinned a current bundle rather than suppressing the warning.
- API exception middleware ordering was corrected so stale approvals return 409 rather than an internal error.
- The pure-rule character policy was corrected to permit a method-parameter comma without expanding the execution surface.
- Readiness was changed to wait for restart recovery, preventing a new operator action before persisted runs are paused.
- The execution gate checks actual artifact bytes as well as recorded hashes, and source changes invalidate pending approvals.

## Independent checks

The executable suite is source-controlled in `tests/Workbench.Checks`. It simulates operator approvals ONLY for testing and retains real run histories and validator output under its printed evidence path.

Use `Verify-Workbench.ps1` to reproduce checks. Use `-Live` only deliberately, with an available permitted Copilot entitlement; failure is expected to remain visible.

## Primary implementation references

- https://github.com/github/copilot-sdk
- https://github.com/github/copilot-sdk/blob/main/docs/auth/authenticate.md
- https://github.com/github/copilot-sdk/blob/main/dotnet/README.md
- https://learn.microsoft.com/en-us/ef/core/providers/sqlite/
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services

No production readiness, independent security audit, actual human approval, or deployment is inferred from a model's narrative.
