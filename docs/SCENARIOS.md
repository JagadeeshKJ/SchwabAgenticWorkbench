# Scenario scripts and expected evidence

Use the dashboard with `demo` selected unless deliberately testing the experimental live adapter.

## Greenfield

Input: shorten absolute HTTP(S) URLs, resolve codes, report aggregate clicks. No aliases or expiration.

Expected sequence:
requirements -> analysis -> design -> plan approval -> three parallel stages -> policy join ->
execution approval -> actual validation -> release approval -> local ZIP.

Inspect:
- `dependency-graph-1.json`: exact dependency IDs.
- `implementation-1-0.json`: fixture label and generated rules.
- `validation-0.json`: real compiler output and HTTP assertions.
- Event history: branch start/finish intervals overlap.
- Downloaded ZIP: runnable source plus artifacts; no compiled caches or credentials.
- `engineering-summary.json`: accepted scope, baseline, repairs, validation references and limitations.
- `workflow.json` and `audit.json` are snapshots at export entry; the live dashboard additionally shows the Release completion committed after ZIP creation.

The default service includes a nullable expiry schema field for later evolution. Baseline behavior rejects expiry input; this is an explicit design trade-off.

## Brownfield: expiration with one controlled defect

Prerequisite: completed greenfield run.

Input: reject expired redirects at `expiresAt <= now`, return 410, and do not increment clicks.
Select `expiration-once`.

1. Impact artifact identifies baseline hash, `LinkRules.IsExpired`, redirect handling and analytics.
2. First candidate intentionally returns false for expiration.
3. Actual external HTTP tests create a soon-expiring link, wait for expiration and request it.
4. Validator reports 302 instead of 410 and a wrong click count.
5. A new Repair node appears and is connected to Policy. Previous execution approval is invalid.
6. Review the repaired expression and approve execution again.
7. Revalidation passes; release remains separately human-approved.

Evidence: two distinct validation JSON files, two execution approvals, repair event, preserved failed output, and measured recovery time.

## Brownfield: rollback / safe-stop variant

Select `expiration-always`. The second candidate still fails. The single repair budget is exhausted.

Expected: SafeStopped, one rollback, no release download. `restored-checkpoint` has the same source hash as the completed baseline. This is real file restoration, not a simulated success message; the failed candidate is retained.

## Ambiguity and dynamic replanning

Input: "Support branded links with useful analytics."

1. Run remains AwaitingClarification with no generation.
2. Clarify: **aliases disabled**, aggregate-only analytics.
3. Let it reach PlanApproval. Note the revision and fingerprint.
4. Use "Revise -> invalidate descendants -> replan", enabling aliases and specifying collision behavior.
5. Revision 1 stages become Superseded; their artifacts remain.
6. Revision 2 introduces AliasPolicy; Implementation now depends on it.
7. A stale approval from revision 1 is rejected with 409.
8. Complete revised gates. External assertions require alias creation and collision 409.

The form exposes a bounded product vocabulary, not arbitrary natural-language feature generation.

## Provider retry

Greenfield with `provider-once` deliberately throws a transient provider error on the first Design attempt.
One retry occurs after backoff; the audit shows both attempts. This does not fake a network outage or charge for an external model.

## Restart

Stop the service at an approval gate and restart it with the same data directory.
It reports Paused and retains the graph, artifacts and event chain. Click Resume; current approval fingerprints still apply only to unchanged inputs. Interrupted Running tasks become Pending and are retried within the same bounded run.

## Optional Copilot run

Select Live Copilot and no fault. The same gates remain. This spends existing Copilot allowance.

Known local result: basic authentication and JSON response worked, but two full greenfield attempts safe-stopped on output-contract problems (a disallowed pattern token, then a missing code field). Those failures were not converted into successful demo output. Demo is the supported evaluator path for this MVP.

## Evidence paths

```text
data/workflow.db
data/runs/<run-id>/revision-<n>/candidate/
data/runs/<run-id>/revision-<n>/evidence/
data/runs/<run-id>/approved-source/
data/runs/<brownfield-id>/restored-checkpoint/
data/runs/<completed-id>/release.zip
```

Acceptance checks use separate `data/checks-<id>` roots. Their operator decisions explicitly say **AUTOMATED TEST - simulated operator**. They are proof of control behavior, not proof of a real person reviewing generated code.
