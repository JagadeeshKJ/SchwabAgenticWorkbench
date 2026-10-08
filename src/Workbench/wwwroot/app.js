const $ = id => document.getElementById(id);
const initialRun = new URLSearchParams(location.search).get("run") || "";
let token = "", selected = /^[a-f0-9]{32}$/.test(initialRun) ? initialRun : "", current = null, busy = false;
const escapeHtml = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
async function api(path, body) {
  const response = await fetch(path, body === undefined ? {} : {
    method: "POST", headers: { "Content-Type": "application/json", "X-Workbench-Token": token }, body: JSON.stringify(body)
  });
  if (!response.ok) { const e = await response.json().catch(() => ({})); throw new Error(e.detail || `HTTP ${response.status}`); }
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}
function showError(error) { $("error").textContent = error.message; $("error").hidden = false; }
async function action(fn) {
  if (busy) return;
  busy = true; $("error").hidden = true;
  try { await fn(); await refresh(); } catch (e) { showError(e); } finally { busy = false; }
}
function el(tag, content, className) {
  const node = document.createElement(tag);
  node.textContent = content;
  if (className) node.className = className;
  return node;
}
async function refresh() {
  const runs = await api("/api/runs");
  const overall = await api("/api/metrics");
  $("overall").replaceChildren(...[
    `Completed / terminal: ${Math.round(overall.successRate * 100)}%`,
    `Runs with retries: ${Math.round(overall.retryFrequency * 100)}%`,
    `Runs with rollback: ${Math.round(overall.rollbackFrequency * 100)}%`,
    `Mean recovery: ${overall.mttrMs == null ? "N/A" : Math.round(overall.mttrMs / 1000) + "s"}`,
    `Runs: ${overall.totalRuns} (${overall.terminalRuns} terminal)`
  ].map(t => el("p", t, "muted")));
  const oldBase = $("baseline").value;
  $("baseline").replaceChildren(new Option("None", ""), ...runs.filter(r => r.scenario === "greenfield" && r.status === "Completed").map(r => new Option(r.id.slice(0, 8), r.id)));
  $("baseline").value = oldBase;
  $("runs").replaceChildren(...runs.map(r => {
    const b = el("button", `${r.scenario} · ${r.id.slice(0, 6)}\n${r.status} · ${r.provider}`, "history" + (r.id === selected ? " selected" : ""));
    b.onclick = () => action(async () => { selected = r.id; $("preview").textContent = "Select an artifact."; });
    return b;
  }));
  if (!selected) return;
  const view = await api(`/api/runs/${selected}`), r = view.run;
  current = r; $("empty").hidden = true; $("detail").hidden = false;
  if (r.rollbackError) showError(new Error(r.rollbackError));
  $("title").textContent = `${r.scenario} — ${r.status}`;
  $("subtitle").textContent = `${r.id} · revision ${r.revision} · baseline ${r.baselineId || "none"}`;
  $("mode").className = r.provider === "demo" ? "badge demo" : "badge live";
  $("mode").textContent = r.provider === "demo" ? "DETERMINISTIC DEMO · actual build and tests" : "LIVE COPILOT · actual build and tests";
  const m = view.metrics;
  $("metrics").replaceChildren(...[
    ["Agent calls", m.agentCalls], ["Provider retries", m.providerRetries], ["Repairs", m.repairAttempts],
    ["Rollbacks", m.rollbacks], ["Validation success", Math.round(m.validationSuccessRate * 100) + "%"],
    ["Elapsed", Math.round(m.endToEndMs / 1000) + "s"], ["Recovery", m.recoveryMs == null ? "N/A" : Math.round(m.recoveryMs / 1000) + "s"],
    ["Audit chain", m.auditChainValid ? "Valid" : "BROKEN"]
  ].map(([name, value]) => { const d = el("div", "", "metric"); d.append(el("strong", value), el("span", name)); return d; }));
  $("revision").textContent = r.revision; $("requirement").textContent = r.requirements.description;
  $("clarification").hidden = r.status !== "AwaitingClarification";
  $("resume-panel").hidden = r.status !== "Paused";
  const terminal = ["Completed", "SafeStopped"].includes(r.status);
  $("stop").disabled = terminal; $("revise").disabled = terminal || r.status === "AwaitingClarification";
  $("graph").replaceChildren(...r.stages.map(s => {
    const d = el("div", "", `node ${s.status}`);
    d.append(el("strong", `${s.id} · ${s.status}`), el("span", `← ${s.dependsOn.join(", ") || "entry"}`), el("small", `attempt ${s.attempts} · ${s.summary}`));
    return d;
  }));
  $("approvals").replaceChildren(...r.stages.filter(s => s.revision === r.revision && s.status === "WaitingApproval").map(s => {
    const panel = el("div", "", "approval");
    panel.append(el("strong", s.kind), el("p", s.summary), el("code", s.fingerprint));
    const button = el("button", "Approve this fingerprint");
    button.onclick = () => action(() => api(`/api/runs/${r.id}/approve/${encodeURIComponent(s.id)}`, {
      revision: r.revision, fingerprint: s.fingerprint, actor: $("operator").value, note: $("note").value
    }));
    panel.append(button); return panel;
  }));
  $("artifacts").replaceChildren(...r.artifacts.map(a => {
    const b = el("button", `r${a.revision} · ${a.name}`, "artifact");
    b.onclick = () => action(async () => {
      const res = await fetch(`/api/runs/${r.id}/artifacts/${a.revision}/${encodeURIComponent(a.name)}`);
      if (!res.ok) throw new Error("Artifact unavailable or integrity check failed.");
      $("preview").textContent = await res.text();
    });
    return b;
  }));
  $("download").hidden = r.status !== "Completed";
  $("download").href = `/api/runs/${r.id}/release`;
  $("events").innerHTML = view.events.slice().reverse().map(e =>
    `<div class="event"><strong>#${e.sequence} ${escapeHtml(e.type)}</strong> <small>${escapeHtml(e.node)} · r${e.revision} · ${escapeHtml(e.at)}</small><p>${escapeHtml(e.detail)}</p></div>`).join("");
}
$("create").onclick = () => action(async () => {
  const run = await api("/api/runs", { scenario: $("scenario").value, provider: $("provider").value,
    baselineId: $("baseline").value || null, fault: $("fault").value });
  selected = run.id;
});
$("clarify").onclick = () => action(() => api(`/api/runs/${selected}/clarify`, {
  expectedRevision: current.revision, aliases: $("clarify-alias").checked, description: $("clarify-text").value, actor: $("operator").value
}));
$("revise").onclick = () => action(() => api(`/api/runs/${selected}/revise`, {
  expectedRevision: current.revision, aliases: $("revision-alias").checked, description: $("revision-text").value, actor: $("operator").value
}));
$("stop").onclick = () => action(() => api(`/api/runs/${selected}/stop`, {}));
$("resume").onclick = () => action(() => api(`/api/runs/${selected}/resume`, {}));
$("source").onclick = () => action(async () => {
  const files = await api(`/api/runs/${selected}/source`);
  $("preview").textContent = files.map(f => `=== ${f.name} ===\n${f.content}`).join("\n\n");
});
(async () => {
  try { token = (await api("/api/session")).token; await refresh(); }
  catch (e) { showError(e); }
  setInterval(() => { if (!busy) refresh().catch(showError); }, 1500);
})();
