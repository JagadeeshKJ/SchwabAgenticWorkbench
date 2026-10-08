# Deliberate limitations and trade-offs

1. **Demo is deterministic.** It produces trusted scaffolding and a small generated rules file. It does not pretend to synthesize arbitrary systems. The optional LLM uses the same provider contract and is experimental.
2. **Copilot integration is time-boxed.** The supported SDK connects through normal CLI credentials. Authentication and a minimal request worked. Two complete live attempts failed strict generation constraints and safe-stopped; no guarantee of live completion or output quality is made.
3. **Single operator/process.** The local mutation token and operator label are not enterprise authentication, RBAC or nonrepudiation. SQLite plus a single-instance file lock is chosen for reproducibility, not horizontal scale.
4. **No OS sandbox.** Workspace separation, token restrictions and trusted project scaffolding narrow risk but cannot replace a VM/container/OS sandbox for arbitrary generated code. Execution requires explicit review and approval.
5. **No actual deployment.** Release means a reviewed local ZIP. No public repository, external service mutation, production migration or email sending is implemented.
6. **Source-only rollback.** The approved baseline is immutable by application convention and checked by hashes. Rollback restores source; it does not undo external effects or claim transactional rollback across services.
7. **Audit-grade structure, not enterprise storage.** Hash-linked events, artifacts and revisions provide traceability; a database administrator can alter data and recompute hashes. External anchoring, append-only storage and retention controls are future work.
8. **Bounded domain.** Requirement revision toggles supported alias behavior. The planner deterministically compiles a validated graph around accepted feature flags. Natural-language descriptions are context, not authorization for unrestricted changes.
9. **Tests are deliberately independent.** AI may draft test plans, but the orchestrator-owned build/HTTP harness is the authority. The implementation agent cannot relax the acceptance checks.
10. **Simple product.** No user accounts, multi-tenancy, vanity domains, personal analytics, distributed cache, deleted-code recycling, expiry cleanup jobs or production abuse detection. Demo creation rate limiting is in memory.
11. **Clock-based expiry test.** The external HTTP test waits past a short expiry. Very overloaded machines could fail to create the short-lived link in time; a more advanced harness would inject a controllable clock.
12. **Restart and external exactly-once effects.** Completed tasks persist. Interrupted tasks may rerun with new attempt numbers. No exactly-once guarantee across arbitrary tools is claimed. Checkpoint copies verify existing bytes; release is staged then atomically renamed. This is local crash tolerance, not a distributed transaction. A rare interruption between an artifact write and its state commit can produce an immutable-file conflict and safely stop the run; it never invents completion.
13. **Metrics include human waits and deliberate failures.** They are useful execution evidence, not service-level objectives. MTTR reports only recovered runs.
14. **No automatic runtime substitution.** If live mode fails, the operator must start an explicitly labelled demo run. No errors are disguised as success.

## Production evolution

Replace local identity with enterprise authentication; move workers into sandboxed execution environments; persist job leases/heartbeats for multi-host scheduling; add structured-output schemas and versioned model evaluation; externalize audit anchoring; add deployment approvals with typed rollback strategies; use production database migrations; add provider spend caps and tracing exporters.

These are future work, not implemented features.
