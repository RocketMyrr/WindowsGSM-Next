// Agent settings → Setup backup: WindowsGSM's own setup (accounts, settings, automations, channels, the Discord
// bot, templates, plugins, each server's settings) in one file — for a new PC, or after a disk dies. Game files
// aren't in it: each server's Backups tab has those.

import { h, icon, clear } from "../dom.js";
import { api, get, post, del, upload } from "../api.js";
import { field, input, toggle, toast, toastError, busy, confirm } from "../ui.js";

export function setupBackupPanel(machine) {
    const base = `/machines/${encodeURIComponent(machine)}`;
    const body = h("div", { class: "panel-body stack" });
    const section = h("section", { class: "panel settings-section" },
        h("div", { class: "panel-head" }, icon("save"), h("h3", { text: "Setup backup" }), h("span", { class: "sub", text: "WindowsGSM's own setup in one file" })),
        body);
    paint();
    return section;

    async function paint() {
        let state = { pending: null, last: null };
        try { state = await get(base + "/setup-restore"); } catch { /* shown as nothing pending */ }
        clear(body).append(...[
            h("p", { class: "small muted", text: "Accounts, agent settings, automations, notification channels, the Discord bot, off-site settings, templates, plugins and each server's settings. Not game files — each server's Backups tab has those. Keep the file somewhere safe: it holds everyone's accounts." }),
            state.pending ? pendingBox(state.pending) : null,
            exportBox(),
            restoreBox(),
            state.last ? lastBox(state.last) : null,
        ].filter(Boolean));
    }

    // ── Back up ──
    function exportBox() {
        const pass = input({ type: "password", autocomplete: "new-password", placeholder: "At least 8 characters" });
        const again = input({ type: "password", autocomplete: "new-password" });
        const passFields = h("div", { class: "form-grid", hidden: true },
            field("Passphrase", pass, { hint: "You'll need it to restore. It isn't saved anywhere — WindowsGSM can't recover it." }),
            field("Passphrase again", again));
        const secrets = toggle("Include passwords and tokens", false, {
            hint: "Discord bot token, webhook addresses, off-site and Steam passwords, the hub link — encrypted with a passphrase. Without them, you enter them again after restoring.",
            onChange: on => { passFields.hidden = !on; },
        });
        const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
            let passphrase = null;
            if (secrets.input.checked) {
                if (pass.value.length < 8) { toast("Use a passphrase of at least 8 characters", { type: "warn" }); return; }
                if (pass.value !== again.value) { toast("The passphrases don't match", { type: "warn" }); return; }
                passphrase = pass.value;
            }
            const res = await api(base + "/setup-backup", { method: "POST", body: { passphrase }, raw: true });
            if (!res.ok) { let msg = `Backup failed (${res.status}).`; try { msg = (await res.json()).error || msg; } catch { } throw new Error(msg); }
            const name = /filename="?([^";]+)"?/.exec(res.headers.get("content-disposition") || "")?.[1] || "wgsm-setup.zip";
            save(await res.blob(), name);
            const left = Number(res.headers.get("x-wgsm-secrets-left-out") || 0), inc = Number(res.headers.get("x-wgsm-secrets-included") || 0);
            toast("Setup backup downloaded", { type: "good", text: passphrase ? `${inc} password(s) and token(s) included, protected by your passphrase.` : left ? `${left} password(s) and token(s) left out — you'll enter them again after restoring.` : "" });
            pass.value = again.value = "";
        }, "Couldn't make the backup") }, icon("download"), "Download setup backup");
        return h("div", { class: "stack" }, h("h4", { text: "Back up" }), secrets, passFields, h("div", { class: "row" }, go));
    }

    function save(blob, name) {
        const url = URL.createObjectURL(blob);
        const a = h("a", { href: url, download: name, hidden: true });
        document.body.append(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
    }

    // ── Restore ──
    function restoreBox() {
        const file = h("input", { type: "file", accept: ".zip,application/zip", class: "input" });
        const pass = input({ type: "password", autocomplete: "off", placeholder: "Only if the backup includes passwords" });
        const identity = toggle("This PC replaces the one the backup came from", false, {
            hint: "Takes over its machine id and hub membership — for a PC that's gone. Leave off when copying a setup to another PC that's also in use.",
        });
        const go = h("button", { class: "btn", onclick: async () => {
            if (!file.files.length) { toast("Choose the setup backup (.zip) first", { type: "warn" }); return; }
            const ok = await confirm({ title: "Restore this setup?", danger: true, confirmLabel: "Restore",
                message: "This PC's accounts and settings are replaced by the backup's when the agent next restarts. The current ones are saved in the backups folder first. Game servers keep running." });
            if (!ok) return;
            await busy(go, async () => {
                const form = new FormData();
                form.append("file", file.files[0]);
                if (pass.value) form.append("passphrase", pass.value);
                form.append("keepIdentity", identity.input.checked ? "true" : "false");
                const r = await upload(base + "/setup-restore", form);
                pass.value = "";
                const notes = [`${r.files} file(s) from ${r.from} (${new Date(r.created).toLocaleString()}, WindowsGSM ${r.version}).`];
                if (r.secretsLeftOut) notes.push(`${r.secretsLeftOut} password(s) and token(s) aren't in it — enter them again afterwards.`);
                if (r.serversSkipped?.length) notes.push(`Settings for server(s) ${r.serversSkipped.join(", ")} are left out: they aren't on this PC.`);
                toast("Ready to restore", { type: "good", text: notes.join(" "), timeout: 12000 });
                paint();
            }, "Couldn't use that backup");
        } }, icon("restore"), "Restore…");
        return h("div", { class: "stack" }, h("h4", { text: "Restore" }),
            h("div", { class: "form-grid" }, field("Setup backup", file), field("Passphrase", pass)), identity, h("div", { class: "row" }, go));
    }

    function pendingBox(p) {
        const restart = h("button", { class: "btn primary sm", onclick: () => busy(restart, async () => {
            await post(base + "/agent/restart");
            toast("Restarting the agent", { type: "info", text: "Back in a few seconds with the restored setup. Game servers keep running. You may need to sign in again.", timeout: 9000 });
        }, "Couldn't restart the agent") }, icon("restart"), "Restart the agent now");
        const cancel = h("button", { class: "btn ghost sm", onclick: () => busy(cancel, async () => { await del(base + "/setup-restore"); toast("Restore cancelled", { type: "info" }); paint(); }) }, "Cancel");
        return h("div", { class: "callout warn" }, icon("restore"), h("div", { class: "grow stack tight" },
            h("span", { text: `A restore from ${p.from} (${new Date(p.created).toLocaleString()}) is waiting: it's applied when the agent restarts.` + (p.keepIdentity ? " This PC takes over that machine's identity." : "") }),
            h("div", { class: "row" }, restart, cancel)));
    }

    function lastBox(r) {
        return h("div", { class: "callout info" }, icon("info"), h("div", { class: "grow stack tight" },
            h("span", { text: `Last restored ${new Date(r.restoredAt).toLocaleString()} from ${r.from} — ${r.files} file(s).` + (r.settingsBackup ? ` The settings it replaced are in ${r.settingsBackup.split(/[\\/]/).pop()}.` : "") }),
            ...(r.notes || []).map(n => h("span", { class: "small", text: n }))));
    }
}
