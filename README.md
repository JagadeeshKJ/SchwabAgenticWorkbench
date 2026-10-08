# Agentic SDLC Workbench

A runnable, deliberately small .NET prototype demonstrating **governed software-engineering orchestration** around a URL shortener.

**Start with deterministic demo mode.** It uses explicitly labelled templates, not a pretend LLM. Generated source is still compiled and exercised through real HTTP assertions. Copilot is an optional, experimental adapter: authentication and a minimal request worked locally, but two full live attempts failed the bounded generation contract and safely stopped. **Live mode is not required to evaluate the complete orchestration.**

## Run

Prerequisites: .NET **10 SDK**, Windows/Linux/macOS, and package-feed access on the first restore. No cloud account, API key, Docker, Node, database server or subscription is required for demo mode.

On Windows, from the repository root:

```powershell
.\Start-Workbench.ps1
```

Open **http://127.0.0.1:5187**. Keep that terminal open. Stop with Ctrl+C.

Alternatively, including on Linux/macOS:

```text
cd src/Workbench
dotnet run --configuration Release
```

The service is loopback-only. If the port is occupied, use `.\Start-Workbench.ps1 -Port 5188`. Do not bypass a device policy that blocks execution.

SQLite state and generated artifacts live under the repository's `data` directory. The first database creation is automatic. A single-instance file lock prevents two hosts sharing that data directory. Existing runs survive restart; unfinished runs require **Resume** before further execution. Never erase `data` to simulate a restart.

## Ten-minute evaluator walkthrough

1. Create **greenfield**, runtime **demo**, fault **none**. Review and approve the plan.
2. Observe **Implementation, Tests, Documentation** running concurrently, followed by the policy join.
3. Select **Inspect candidate source**. Approve execution only after reviewing it.
4. Open `validation-0.json`: actual build exit code, HTTP assertions, source fingerprint and duration.
5. Approve release, then download the source/evidence ZIP. This is a **local export**, not deployment.
6. Create **brownfield**, select the completed greenfield baseline, and choose **Expiration defect -> repair**.
7. The first real validator detects HTTP 302 instead of 410 and a wrong click count. A repair node is added. A **new execution approval** is required before revalidation.
8. Create **ambiguous**. Submit clarification with aliases disabled. Before completing the run, revise to enable branded aliases. Observe a new graph revision and **AliasPolicy** dependency; old tasks become **Superseded** and their approvals no longer apply.
9. For rollback, use brownfield with **Persistent defect**. It exhausts its one repair, restores the approved baseline source and safe-stops without release.
10. For retries, use greenfield with **Transient provider**. One deliberate provider failure triggers one bounded retry.

See [scenario instructions](docs/SCENARIOS.md), [architecture](docs/ARCHITECTURE.md), and [limitations](docs/LIMITATIONS.md).
The [verification record](docs/VERIFICATION.md) identifies the actual final scenario runs and their results.

## Verify

```powershell
.\Verify-Workbench.ps1
```

This compiles the application and runs a dependency-free .NET acceptance-check executable. It starts its own loopback host and actual generated APIs, uses a separate `data\checks-<id>` database, verifies all scenarios, terminates only its own processes, and retains evidence. **Approvals in this suite are explicitly labelled simulated test approvals**, not real human review.

Checks cover graph validation, unsafe-source rejection, actual parallelism, operator-token/origin checks, stale approvals, real compilation and HTTP contract validation, repair/revalidation, bounded rollback, audit tampering, requirement revisions, restart/resume and safe-stop fencing.

SQL and runtime dependencies are pinned. The SQLite native bundle is explicitly pinned to 3.0.5 after the initial EF transitive bundle was flagged by NuGet audit. No vulnerability warning was suppressed.

## Product API

The approved ZIP contains `shortener/`. Run `dotnet run` there; its default URL is **http://127.0.0.1:5190**.

| Operation | Endpoint | Behavior |
|---|---|---|
| Create | `POST /api/links` | JSON `url`, optional `alias`, optional `expiresAt`; 201 or explicit error |
| Redirect | `GET /r/{code}` | 302, `Location`, `Cache-Control: no-store`; 404 unknown, 410 expired |
| Statistics | `GET /api/links/{code}/stats` | Aggregate clicks and last access, no personal analytics |
| Health | `GET /health` | Process readiness |

Expiration and aliases are enabled by approved scenario requirements. Disabled features reject their corresponding creation inputs. Expiry is inclusive and checked before incrementing analytics. The schema reserves nullable `ExpiresAt` from greenfield; brownfield changes behavior, not a destructive schema migration. Destination URLs are validated but **never fetched by the service**.

## Optional live Copilot

The `IAgentProvider` boundary has two implementations: `DemoAgentProvider` and `CopilotAgentProvider`. The same graph/governance/validation code runs for both.

The live adapter uses **GitHub.Copilot.SDK 1.0.14**, the installed Copilot CLI and its normal signed-in credentials. It requires no new paid provider API. It still consumes the user's Copilot allowance and is subject to their organization policies. No tokens are extracted or embedded.

1. Install the official Copilot CLI if needed, and sign in yourself.
2. If the executable cannot be resolved, set `CopilotPath` to its path.
3. Optionally set `CopilotModel` to a model actually available to your account; the default is `auto`.
4. Start the workbench and explicitly select **Live Copilot**. All built-in, MCP and custom tools are excluded from the model session.

Do not assume the model menu in another product grants the same SDK access. Runtime/provider errors **safe-stop**; they never switch the run to demo or create fake passing evidence. Start a new explicitly labelled demo run if live mode is unavailable.

The optional integration check is `.\Verify-Workbench.ps1 -Live`. It makes real model requests and may fail; it is not part of the offline/demo acceptance gate.

## Project structure

```text
src/Workbench/
  Domain.cs               Run/stage states, dependency graph, contracts
  RunStore.cs             Transactional SQLite snapshots and audit chain
  Orchestrator.cs         Scheduler, gates, revisions, retry/repair/stop
  Providers.cs            One provider boundary, role-specific prompts
  Workspace.cs            Versioned candidates, hashes, policy, checkpoints
  ValidationRunner.cs     Bounded build and external HTTP assertions
  Metrics.cs              Defined per-run and aggregate reliability metrics
  Templates/              Trusted product scaffold; no arbitrary MSBuild input
  wwwroot/                Minimal local evaluator dashboard
tests/Workbench.Checks/   Executable scenario/integration checks
tools/ProviderProbe/      Small SDK authentication/response probe
docs/                    Architecture, scenarios, limitations and AI-use record
```

## Evidence and ownership

This work was AI-assisted. The candidate should inspect, run and understand it before submission. The demo implementation is intentionally template-backed; only a bounded rules file is delegated to the optional live model. The runtime does not claim to generate arbitrary production applications.

No employer PDF, interview transcript, résumé, credentials or proprietary source is included. No repository or artifact was published. The "operator name" is a local audit label, not enterprise identity authentication.

Create a source-only submission ZIP with `.\Package-Submission.ps1`. Review it before sending. The script excludes all local run data, credentials, caches and compiled outputs; it never uploads anything.
