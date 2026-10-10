// Health checks: is a machine ready to run game servers? (Folders, tools, disk, plugins, network.)

import { h, icon, clear } from "../dom.js";
import { get, post } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { loading, empty, busy, toast } from "../ui.js";
import { serverPath } from "../router.js";
import { machinePicker } from "./machines.js";
import { reportUrl } from "../report.js";

const ORDER = { Fail: 0, Warning: 1, Info: 2, Pass: 3 };
const ICONS = { Pass: "checkCircle", Fail: "xCircle", Warning: "warn", Info: "info" };

export default async function health(host, { query }) {
    setCrumbs({ label: "Health checks" });
    let machine = store.machines.has(query.get("machine")) ? query.get("machine") : store.localId;
    const title = h("p");
    const paintTitle = () => { title.textContent = `Is ${store.machineName(machine)} ready to run game servers?`; };
    const picker = machinePicker(machine, m => { machine = m; history.replaceState({}, "", "/health?machine=" + encodeURIComponent(m)); paintTitle(); busy(rerun, run); }, { onlineOnly: true });
    paintTitle();
    const summary = h("div", { class: "health-summary" });
    const groups = h("div", { class: "stack loose" });
    const rerun = h("button", { class: "btn", onclick: () => busy(rerun, run) }, icon("refresh"), "Run again");
    // For a bug report: versions, health, recent logs and settings in one zip (secrets taken out). Admins only.
    const diagnostics = store.me.canManageUsers ? h("a", { class: "btn", download: "",
        title: "One zip for a bug report: versions, these checks, recent logs and settings. Passwords, tokens and keys are taken out; server names, folders and IP addresses stay — look through it before posting it publicly.",
        onclick: e => { e.currentTarget.href = `/api/v2/machines/${encodeURIComponent(machine)}/diagnostics`; } }, icon("download"), "Export diagnostics") : null;
    const report = h("a", { class: "btn", href: reportUrl(), target: "_blank", rel: "noopener", title: "Opens GitHub's bug-report form in your browser, with this version filled in. Attach the diagnostics zip there." }, icon("link"), "Report a problem");
    host.append(
        h("div", { class: "page-head" }, h("div", {}, h("h1", { text: "Health checks" }), title), h("div", { class: "actions" }, picker, diagnostics, report, rerun)),
        summary, groups);

    async function run() {
        clear(groups).append(loading("Checking…"));
        const base = "/machines/" + encodeURIComponent(machine);
        const [checks, broken] = await Promise.all([get(base + "/readiness"), get(base + "/plugins/broken").catch(() => [])]);
        const count = s => checks.filter(c => c.status === s).length;
        clear(summary).append(
            stat("checkCircle", "Passed", count("Pass"), "green"),
            stat("warn", "Warnings", count("Warning"), "amber"),
            stat("xCircle", "Problems", count("Fail"), "rose"),
            stat("info", "Notes", count("Info"), "cyan"));
        clear(groups);
        const scopes = [...new Set(checks.map(c => c.scope))];
        for (const scope of scopes) {
            const items = checks.filter(c => c.scope === scope).sort((a, b) => ORDER[a.status] - ORDER[b.status]);
            groups.append(h("section", { class: "panel" },
                h("div", { class: "panel-head" }, h("h2", { text: scope === "App" ? store.machineName(machine) : scope })),
                h("div", { class: "panel-body flush" }, ...items.map(c => h("div", { class: ["list-item check-row", c.status.toLowerCase()] },
                    icon(ICONS[c.status]), h("div", { class: "grow" }, h("b", { text: c.name }), h("div", { class: "small muted", text: c.message })),
                    c.name === "Game servers through Windows Firewall" && c.status === "Warning" && store.me.canManageUsers ? firewallButton() : null)))));
        }
        if (broken.length) {
            groups.append(h("section", { class: "panel" },
                h("div", { class: "panel-head" }, h("h2", { text: "Plugins that didn't load" })),
                h("div", { class: "panel-body flush" }, ...broken.map(p => h("div", { class: "list-item check-row warning" }, icon("puzzle"),
                    h("div", { class: "grow" }, h("b", { text: p.fileName }), h("div", { class: "small muted mono", text: p.error || "Unknown error" })))))));
        }
        groups.append(h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h2", { text: "Servers" }), h("span", { class: "sub", text: "Each server's own checks are on its Overview tab." })),
            h("div", { class: "panel-body flush" }, ...(store.sortedServers(machine).length ? store.sortedServers(machine).map(s => h("a", { class: "list-item clickable server-link", href: serverPath(s.machine, s.id) },
                icon("servers"), h("span", { class: "grow", text: s.name }), icon("chevronRight"))) : [empty("servers", "No servers yet", "")]))));
    }

    // One Windows administrator prompt (on that machine's screen) for every server that needs a rule.
    function firewallButton() {
        const b = h("button", { class: "btn primary sm", title: "Windows asks for administrator approval on that computer's screen", onclick: () => busy(b, async () => {
            const r = await post("/machines/" + encodeURIComponent(machine) + "/firewall");
            toast(`Allowed ${r.allowed} server${r.allowed === 1 ? "" : "s"} through Windows Firewall`, { type: "good" });
            await run();
        }, "Couldn't change Windows Firewall") }, icon("shield"), "Allow all through firewall");
        return b;
    }

    function stat(ic, label, n, tone) {
        return h("div", { class: ["health-stat", tone, n === 0 && "zero"] }, icon(ic), h("b", { class: "num", text: n }), h("span", { text: label }));
    }

    await run();
}
