// Updating WindowsGSM itself — one machine's panel (Agent settings for this one, Machines → Updates for others)
// and "update every machine" for the hub. Game servers keep running while the agent restarts.

import { h, icon, clear, append, timeAgo, fmtDateTime } from "../dom.js";
import { get, post, put } from "../api.js";
import { store } from "../store.js";
import { toast, toastError, busy, confirm, field, input, toggle, progressBar, setProgress, loading } from "../ui.js";

const base = machine => `/machines/${encodeURIComponent(machine)}/agent/update`;

/** The Updates panel for one machine. Keeps itself current while an update runs. */
export function updatesPanel(machine, scope) {
    const owner = !!store.me.isOwner;
    const body = h("div", { class: "stack" }, loading());
    let timer = null;
    let restartingFrom = null;

    async function load() {
        let u;
        try { u = await get(base(machine)); }
        catch (e) {
            // While the agent restarts it can't answer — that's expected during an update.
            if (restartingFrom) { schedule(2000); return; }
            clear(body).append(h("div", { class: "small faint", text: e.message }));
            return;
        }
        if (restartingFrom && u.current !== restartingFrom) {
            restartingFrom = null;
            toast(`Now running WindowsGSM ${u.current}`, { type: "good", text: "Game servers kept running." });
            if (machine === store.localId) { setTimeout(() => location.reload(), 1200); }
        }
        paint(u);
        if (["Downloading", "Installing", "Restarting", "Checking"].includes(u.state) || restartingFrom) schedule(u.state === "Restarting" ? 2000 : 1000);
    }

    function schedule(ms) {
        clearTimeout(timer);
        timer = setTimeout(() => { if (!scope || scope.alive) load(); }, ms);
    }
    if (scope) scope.add(() => clearTimeout(timer));

    function paint(u) {
        const busyState = ["Downloading", "Installing", "Restarting"].includes(u.state);
        const status = !u.latest ? (u.checkedAt ? "Couldn't find any releases on the feed." : "Not checked yet.")
            : u.available ? `WindowsGSM ${u.latest} is available${u.published ? " — released " + timeAgo(u.published) : ""}.`
                : "You're up to date.";
        const bar = progressBar(u.percent ?? null);
        if (u.state === "Downloading") setProgress(bar, u.percent ?? 0);
        const doing = { Downloading: `Downloading ${u.latest}… ${u.percent ?? 0}%`, Installing: "Unpacking and checking…", Restarting: "Restarting the agent — game servers keep running. This page reconnects by itself." }[u.state];

        const check = h("button", { class: "btn sm", disabled: busyState, onclick: e => busy(e.currentTarget, async () => paint(await post(base(machine) + "/check")), "Couldn't check") }, icon("refresh"), "Check now");
        const apply = owner && u.installed && u.available ? h("button", { class: "btn primary sm", disabled: busyState, onclick: async e => {
            if (!(await confirm({ title: `Update to WindowsGSM ${u.latest}?`, message: "The agent restarts on the new version in about a minute. Game servers keep running, and you can go back to this version afterwards. (A server whose console shows in the panel keeps running too; its new console output appears again after that server's next restart — RCON works throughout.)", confirmLabel: "Update" }))) return;
            await busy(e.currentTarget, async () => { await post(base(machine) + "/apply"); restartingFrom = u.current; schedule(500); }, "Couldn't start the update");
        } }, icon("download"), `Update to ${u.latest}`) : null;
        const rollback = owner && u.installed && u.previous && !busyState ? h("button", { class: "btn ghost sm", onclick: async e => {
            if (!(await confirm({ title: `Go back to WindowsGSM ${u.previous}?`, message: "The agent restarts on the previous version. Game servers keep running.", confirmLabel: "Go back" }))) return;
            await busy(e.currentTarget, async () => { await post(base(machine) + "/rollback"); restartingFrom = u.current; schedule(1500); }, "Couldn't go back");
        } }, icon("restore"), `Go back to ${u.previous}`) : null;

        append(clear(body), [
            h("div", { class: "row wrap update-head" },
                h("div", { class: "grow" }, h("b", { text: `WindowsGSM ${u.current}` }), h("div", { class: ["small", u.available ? "accent-text" : "muted"], text: status }),
                    u.checkedAt ? h("div", { class: "tiny faint", title: fmtDateTime(u.checkedAt), text: "Checked " + timeAgo(u.checkedAt) }) : null),
                check, rollback, apply),
            doing ? h("div", { class: "stack tight" }, h("div", { class: "small", text: doing }), u.state === "Downloading" ? bar : null) : null,
            u.error && u.state === "Error" ? h("div", { class: "callout bad" }, icon("warn"), h("span", { text: u.error })) : null,
            !u.installed ? h("div", { class: "callout info" }, icon("info"), h("span", { text: "This copy runs from a build folder, so it can't update itself. Install WindowsGSM with setup to get one-click updates." })) : null,
            u.available && u.notes ? h("details", { class: "update-notes" }, h("summary", { text: "What's new" }), h("pre", { text: readable(u.notes) })) : null,
            owner ? feedSettings(u) : null]);
    }

    function feedSettings(u) {
        const repo = input({ class: "input mono", value: u.repo, placeholder: "owner/repository" });
        const pre = toggle("Include pre-releases (alpha, beta)", u.prerelease);
        const save = h("button", { class: "btn sm", onclick: () => busy(save, async () => { paint(await put(base(machine) + "/settings", { repo: repo.value.trim(), prerelease: pre.input.checked })); toast("Update feed saved", { type: "good", timeout: 2000 }); }, "Couldn't save") }, icon("save"), "Save");
        return h("details", { class: "update-feed" }, h("summary", { text: "Where updates come from" }),
            h("div", { class: "stack" }, field("GitHub repository", repo, { hint: "Releases with WindowsGSM-<version>.zip and a .sha256 beside it. Or the address of your own JSON feed." }), pre, h("div", { class: "row" }, h("span", { class: "spacer" }), save)));
    }

    load();
    return body;
}

/** Hub: installs the latest version on every online machine that has one waiting. */
export async function updateAll() {
    const machines = [...store.machines.values()].filter(m => m.online !== false);
    const status = await Promise.all(machines.map(async m => ({ m, u: await get(base(m.id)).catch(() => null) })));
    const waiting = status.filter(x => x.u && x.u.installed && x.u.available);
    if (!waiting.length) { toast("Every machine is up to date", { type: "good", text: "Or can't update itself (not installed with setup)." }); return; }
    const list = waiting.map(x => `${x.m.name} (${x.u.current} → ${x.u.latest})`).join(", ");
    if (!(await confirm({ title: `Update ${waiting.length} machine${waiting.length === 1 ? "" : "s"}?`, message: `${list}. Each agent restarts on the new version; game servers keep running.`, confirmLabel: "Update all" }))) return;
    // Members first, this machine last — it's the one serving this page.
    waiting.sort((a, b) => (a.m.isLocal ? 1 : 0) - (b.m.isLocal ? 1 : 0));
    const failed = [];
    for (const x of waiting) {
        try { await post(base(x.m.id) + "/apply"); } catch (e) { failed.push(`${x.m.name}: ${e.message}`); }
    }
    if (failed.length) toastError(new Error(failed.join(" · ")), "Some machines didn't start updating");
    else toast("Updating", { type: "good", text: "Machines reconnect on the new version in about a minute." });
}

/** Release notes are Markdown: shown as plain, tidy text (no ** or `, links as their text, bullets as •). */
function readable(md) {
    return md.replace(/\r\n/g, "\n")
        .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
        .replace(/\*\*([^*]+)\*\*/g, "$1")
        .replace(/`([^`]+)`/g, "$1")
        .replace(/^#{1,6}\s+/gm, "")
        .replace(/^(\s*)[-*] /gm, "$1• ")
        .trim();
}
