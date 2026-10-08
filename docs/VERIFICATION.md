# Local verification record

Date: **7 October 2026**. Environment: Windows, .NET SDK 10.0.401.

Command: `.\Verify-Workbench.ps1`

Result: **30 assertions passed**, build with zero warnings and zero errors. Dashboard JavaScript passed `node --check`; a separate headless Edge render showed the real task states, dependencies, controls and metrics. NuGet audit reported no known vulnerabilities for the resolved application dependency graph at verification time; this is not a security audit.

Evidence root printed by the final acceptance run:

```text
data/checks-09d46c1967bf47c09aede30c602ee1ac
```

| Scenario | Run ID | Observed result |
|---|---|---|
| Greenfield + provider fault | `7dd0d811050142569297b37de8e00582` | One provider retry; build exit 0; 7 HTTP assertions pass; approved export |
| Brownfield expiry + repair | `a141a43d41aa45c7a918d2c10016173c` | First validation fails; repair; new execution approval; second validation passes all 9 HTTP assertions |
| Brownfield persistent defect | `1a76e65b24374ec8a187b5bf58799011` | Both validations fail; one repair exhausted; exact baseline source restored; SafeStopped |
| Ambiguous/revised requirement | `e3c158e1f126483ca79c4df2010ad5c2` | Clarification; revision 2 introduces AliasPolicy; old approvals rejected; 7 HTTP assertions pass |
| Restart/resume | `77331b10e43042339495dabec6d26bbd` | Host killed and restarted; run paused; explicit resume; completed successfully |
| Operator stop | `21ac355aa18941898c4f2c7f3dc0ded8` | SafeStopped; late outputs cannot complete the run |
| Changed candidate bytes | `3f3844c82f7e435dad3eb47b59d213ae` | Pending execution approval rejected after source mutation |
| Corrupt rollback baseline | `50781e745e2d4d519875fc5fc0876f64` | Explicit rollback error and SafeStopped; no false rollback success |

All approvals in the automated suite are labelled simulated test approvals. The source ZIP intentionally excludes local `data`; rerunning the checks creates fresh evidence and fresh IDs on the evaluator's machine.

## Additional checks in the suite

- Cyclic graphs and disallowed generated code rejected.
- Token-less and cross-origin mutations rejected.
- Implementation, test planning and documentation execution intervals overlap.
- Audit hash verification rejects altered event content.
- Validation evidence comes from compiler exits and HTTP observations, not agent prose.
- Release contains code, OpenAPI schema, final engineering summary and traceability artifacts.
- Requirement revisions retain superseded artifact history.
- Aggregate retry/rollback metrics and measured MTTR are exposed.

## Live adapter results

The official SDK probe authenticated and returned `{"status":"ok"}`. Two complete live greenfield attempts safe-stopped on generation-contract failures. Their failures were not retried indefinitely or silently replaced with demo output.

The deterministic demo is therefore the verified MVP path. Improving live structured-output reliability is a documented follow-on, not a claim of completed work.
