// Users & access: accounts, roles, and exactly which servers each person may touch.

import { h, icon, clear, timeAgo, gameTile } from "../dom.js";
import { get, post, put, del } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { modal, field, input, select, toggle, toast, toastError, busy, confirm, empty, segmented } from "../ui.js";

const ROLES = [
    { value: "Owner", label: "Owner", text: "Everything, including admins, security and agent settings." },
    { value: "Admin", label: "Admin", text: "Everything on every server; manages operators, viewers and members." },
    { value: "Operator", label: "Operator", text: "Start, stop, restart, update, back up and use the console on every server." },
    { value: "Viewer", label: "Viewer", text: "Sees everything, changes nothing." },
    { value: "Member", label: "Member", text: "Only what you grant below — e.g. just their own server." },
];
const RANK = { Member: 0, Viewer: 1, Operator: 2, Admin: 3, Owner: 4 };
const CAPS = [
    ["View", "See it"], ["Start", "Start"], ["Stop", "Stop"], ["Restart", "Restart"], ["Kill", "Force stop"], ["Update", "Update"],
    ["Backup", "Back up"], ["Restore", "Restore"], ["Console", "Console"], ["EditConfig", "Settings"], ["Files", "Files"],
    ["Addons", "Add-ons"], ["Schedules", "Schedules"], ["Delete", "Delete"],
];
// Permissions that let someone change the programs a server runs — on this PC, as WindowsGSM.
const RUNS_CODE = {
    Files: "Can change any file in the server's folder — including the programs it runs. Only for people you'd trust with this PC.",
    Addons: "Installs mods and plugins from anywhere — code the server runs. Only for people you'd trust with this PC.",
};
const PRESETS = {
    none: [],
    view: ["View"],
    play: ["View", "Start", "Stop", "Restart", "Console"],
    manage: ["View", "Start", "Stop", "Restart", "Update", "Backup", "Restore", "Console", "EditConfig", "Files", "Addons", "Schedules"],
};

const canManage = (role) => store.me.isOwner || (store.me.canManageUsers && RANK[role] < RANK.Admin);

export default async function users(host, { scope }) {
    setCrumbs({ label: "Users & access" });
    const table = h("div");
    host.append(
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Users & access" }), h("p", { text: "Give friends and co-admins their own sign-in — with access to exactly the servers you choose." })),
            h("div", { class: "actions" }, h("button", { class: "btn primary", onclick: () => edit(null) }, icon("plus"), "Add a person"))),
        h("section", { class: "panel" }, h("div", { class: "panel-body flush" }, table)));

    async function load() {
        const list = await get("/users");
        clear(table);
        if (!list.length) { table.append(empty("users", "No users", "")); return; }
        table.append(h("table", { class: "table" },
            h("thead", {}, h("tr", {}, h("th", { text: "Person" }), h("th", { text: "Role" }), h("th", { text: "Access" }), h("th", { text: "Last sign-in" }), h("th", { class: "actions" }))),
            h("tbody", {}, ...list.map(u => h("tr", { class: !u.enabled ? "disabled-row" : "" },
                h("td", {}, h("span", { class: "row" }, h("span", { class: "avatar", text: u.username.slice(0, 1) }),
                    h("span", {}, h("b", { text: u.username }), u.username === store.me.username ? h("span", { class: "tag accent", text: "You" }) : null,
                        !u.enabled ? h("span", { class: "tag rose", text: "Disabled" }) : null,
                        u.twoFactorEnabled ? h("span", { class: "tag", title: "Two-factor sign-in is on" }, icon("shield"), "2FA") : null))),
                h("td", {}, h("span", { class: ["role-badge", u.role.toLowerCase()], text: u.role })),
                h("td", { class: "small muted", text: accessSummary(u) }),
                h("td", { class: "small muted", text: u.lastLoginAt ? `${timeAgo(u.lastLoginAt)}${u.lastLoginIp ? " · " + u.lastLoginIp : ""}` : "Never" }),
                h("td", { class: "actions" },
                    canManage(u.role) ? h("button", { class: "btn ghost sm", onclick: () => edit(u) }, icon("pencil"), "Edit") : null,
                    canManage(u.role) && u.username !== store.me.username ? h("button", { class: "btn ghost sm icon-only", "aria-label": `Delete ${u.username}`, onclick: () => remove(u) }, icon("trash")) : null))))));
    }

    function accessSummary(u) {
        if (RANK[u.role] >= RANK.Admin) return "All servers, full control";
        const scopes = Object.keys(u.grants || {});
        const base = u.role === "Operator" ? "All servers (day-to-day)" : u.role === "Viewer" ? "All servers (view only)" : "";
        if (!scopes.length) return base || "No servers yet";
        const machineWide = scopes.some(s => s.endsWith("/*"));
        const count = scopes.filter(s => !s.endsWith("/*")).length;
        return [base, machineWide ? (store.multiMachine ? "extra rights on a machine" : "extra rights on this machine") : null, count ? `${count} server${count === 1 ? "" : "s"} granted` : null].filter(Boolean).join(" + ");
    }

    async function remove(u) {
        if (!(await confirm({ title: `Remove ${u.username}?`, message: "They're signed out immediately and can't sign in again.", confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
        try { await del(`/users/${encodeURIComponent(u.username)}`); toast("Removed", { type: "good", text: u.username }); load(); }
        catch (e) { toastError(e); }
    }

    function edit(user) {
        const isNew = !user;

        const name = input({ value: user?.username || "", disabled: !isNew, autocomplete: "off" });
        const nameField = field("Username", name, { help: "What they type to sign in — up to 64 characters (no slashes). It can't be changed later." });
        const pw = input({ type: "password", autocomplete: "new-password", placeholder: isNew ? "" : "Leave empty to keep the current password" });
        const pwField = field(isNew ? "Password" : "Reset password", pw, { hint: "At least 8 characters. Share it with them privately — they can change it after signing in." });
        const roles = ROLES.filter(r => canManage(r.value));
        const role = select(roles.map(r => ({ value: r.value, label: r.label })), user?.role || "Member");
        const roleHint = h("div", { class: "hint" });
        const enabled = toggle("Can sign in", user ? user.enabled : true, { help: "Off locks the account without deleting it — they're signed out everywhere, and their settings stay for when you turn it back on." });
        const grants = new Map(Object.entries(user?.grants || {}).map(([k, v]) => [k, new Set(String(v).split(",").map(s => s.trim()).filter(Boolean))]));
        const grid = h("div", { class: "grant-grid" });
        const expanded = new Set();
        const grantsSection = h("div", { class: "stack" });
        // "May install new servers" is per machine.
        const installBoxes = new Map([...store.machines.keys()].map(m => [m, h("input", { type: "checkbox", checked: grants.get(`${m}/*`)?.has("Install") || false })]));

        function paintRole() {
            roleHint.textContent = ROLES.find(r => r.value === role.value).text;
            grantsSection.hidden = RANK[role.value] >= RANK.Admin;
        }
        role.addEventListener("change", paintRole);

        function paintGrid() {
            const servers = store.sortedServers();
            clear(grid);
            if (!servers.length) { grid.append(h("div", { class: "small faint", text: store.multiMachine ? "No servers on any machine yet." : "No servers on this machine yet." })); return; }
            grid.append(h("div", { class: "grant-row head" }, h("span", { text: "Server" }), h("span", { text: "Access" })));
            let group = null;
            for (const s of servers) {
                if (store.multiMachine && s.machine !== group) {
                    group = s.machine;
                    grid.append(h("div", { class: "grant-row machine" }, icon("machine"), h("b", { text: store.machineName(group) })));
                }
                const scopeKey = `${s.machine}/${s.id}`;
                const set = grants.get(scopeKey) || new Set();
                const chips = h("div", { class: "cap-chips" }, ...CAPS.map(([cap, label]) => {
                    const on = set.has(cap);
                    return h("button", { type: "button", class: ["cap-chip", on && "on"], "aria-pressed": String(on), title: RUNS_CODE[cap] || null, onclick: () => {
                        const cur = grants.get(scopeKey) || new Set();
                        if (cur.has(cap)) cur.delete(cap); else { cur.add(cap); cur.add("View"); }
                        if (cap === "View" && !cur.has("View")) cur.clear();
                        grants.set(scopeKey, cur);
                        paintGrid();
                    } }, label);
                }));
                const preset = segmented([{ value: "none", label: "None" }, { value: "view", label: "View" }, { value: "play", label: "Play" }, { value: "manage", label: "Manage" }],
                    presetOf(set), v => { grants.set(scopeKey, new Set(PRESETS[v])); paintGrid(); });
                // The individual permissions stay tucked away unless you ask for them (or already use a custom mix).
                const custom = !presetOf(set) || expanded.has(scopeKey);
                chips.hidden = !custom;
                const tune = h("button", { type: "button", class: "btn ghost sm", "aria-expanded": String(custom), onclick: () => {
                    if (expanded.has(scopeKey)) expanded.delete(scopeKey); else expanded.add(scopeKey);
                    paintGrid();
                } }, custom ? "Hide details" : "Fine-tune");
                grid.append(h("div", { class: "grant-row" }, h("span", { class: "row truncate" }, gameTile(s.game, s.iconUrl, "sm", s.artUrl), h("b", { class: "truncate", text: s.name })), h("div", { class: "row" }, preset, tune), chips));
            }
        }
        const presetOf = set => Object.entries(PRESETS).find(([, caps]) => caps.length === set.size && caps.every(c => set.has(c)))?.[0] || "";

        grantsSection.append(
            h("div", { class: "upper", text: "Server access" }),
            h("p", { class: "small muted", text: "On top of the role. 'Play' is start/stop/restart and the console — ideal for a friend running their own server. 'Manage' includes Files and Add-ons, which can change the programs a server runs on this PC: give those only to people you'd trust with the PC itself." }),
            grid,
            ...[...installBoxes].map(([m, box]) => h("label", { class: "row small" }, box,
                store.multiMachine ? `May install new servers on ${store.machineName(m)}` : "May install new servers on this machine")));

        paintRole();
        paintGrid();

        modal({
            title: isNew ? "Add a person" : `Edit ${user.username}`, iconName: "user", wide: true,
            body: h("div", { class: "stack" },
                h("div", { class: "form-grid" }, nameField, pwField),
                h("div", { class: "form-grid" }, field("Role", role, { help: ["Owner: everything, including agent settings and other owners.", "Admin: every server, users, plugins and machines.", "Operator: start, stop, restart, console and settings — on the servers you give them.", "Viewer: can look at the servers you give them, but not change anything.", "Member: only what you grant per server below."] }), enabled),
                roleHint,
                grantsSection),
            footer: close => {
                const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
                    nameField.setError(""); pwField.setError("");
                    if (isNew && !name.value.trim()) { nameField.setError("Enter a username."); return; }
                    if ((isNew || pw.value) && pw.value.length < 8) { pwField.setError("Use at least 8 characters."); return; }
                    const out = {};
                    if (RANK[role.value] < RANK.Admin) {
                        for (const [k, set] of grants) if (set.size && !k.endsWith("/*")) out[k] = [...set].join(", ");
                        for (const [m, box] of installBoxes) {
                            const machineCaps = new Set(grants.get(`${m}/*`) || []);
                            if (box.checked) machineCaps.add("Install"); else machineCaps.delete("Install");
                            if (machineCaps.size) out[`${m}/*`] = [...machineCaps].join(", ");
                        }
                        // Keep machine-wide grants for machines this list doesn't show (removed, or not loaded).
                        for (const [k, set] of grants) if (k.endsWith("/*") && !installBoxes.has(k.slice(0, -2)) && set.size) out[k] = [...set].join(", ");
                    }
                    const body = { username: name.value.trim(), role: role.value, enabled: enabled.input.checked, grants: out, password: pw.value || null };
                    if (isNew) await post("/users", body);
                    else await put(`/users/${encodeURIComponent(user.username)}`, body);
                    close(true);
                    toast(isNew ? "Person added" : "Saved", { type: "good", text: body.username });
                    load();
                }, "Couldn't save") }, icon("save"), isNew ? "Add person" : "Save");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), save];
            },
        });
        if (isNew) name.focus();
    }

    await load();
}
