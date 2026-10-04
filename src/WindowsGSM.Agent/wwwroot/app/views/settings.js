// Agent settings (owners): machine name, network access, HTTPS / Let's Encrypt, sign-in length, and
// starting with Windows.

import { h, icon, clear } from "../dom.js";
import { get, post, put } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { field, input, toggle, toast, toastError, busy, segmented, confirm, helpButton } from "../ui.js";
import { updatesPanel } from "./updates.js";
import { steamPanel } from "./steam.js";
import { offsitePanel } from "./offsite.js";

export default async function settings(host, { scope }) {
    setCrumbs({ label: "Agent settings" });
    if (!store.me.isOwner) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only owners can change the agent's settings." })));
        return;
    }
    const [cfg, startup, cert] = await Promise.all([get("/agent/settings"), get("/agent/startup"), get("/agent/certificate").catch(() => null)]);

    const machineName = input({ value: cfg.machineName });
    const port = input({ type: "number", min: 1, max: 65535, value: cfg.port, class: "input num" });
    const network = toggle("Reachable from other computers", cfg.exposeToNetwork, { hint: "Off = this computer only (safest). On = your network — and the internet if you forward the port.", help: [
        "Turn this on to use the panel from your phone or another PC: open http://<this PC's address>:<port> there (the desktop app keeps working either way).",
        "Reaching it from outside your home network also needs the port forwarded on your router — turn on HTTPS first, and two-factor or a passkey on your account.",
        "Restart the agent after changing it."] });
    const sessionHours = input({ type: "number", min: 1, max: 720, value: cfg.sessionHours, class: "input num" });

    let httpsMode = cfg.acmeEnabled ? "acme" : cfg.useHttps ? (cfg.certPath ? "cert" : "self") : "off";
    const certPath = input({ value: cfg.certPath || "", class: "input mono", placeholder: "C:\\certs\\fullchain.pem or .pfx" });
    const keyPath = input({ value: cfg.keyPath || "", class: "input mono", placeholder: "C:\\certs\\privkey.pem (PEM only)" });
    const certPassword = input({ type: "password", placeholder: "Unchanged", autocomplete: "off" });
    const domain = input({ value: cfg.acmeDomain || "", class: "input mono", placeholder: "games.example.com" });
    const email = input({ type: "email", value: cfg.acmeEmail || "", placeholder: "you@example.com" });
    const staging = toggle("Use Let's Encrypt's test servers", cfg.acmeStaging, { hint: "For trying the setup — browsers won't trust these certificates.", help: "Let's Encrypt limits how many real certificates a domain can get per week. Test with this on until it works, then turn it off for a real certificate." });
    const httpsDetails = h("div", { class: "stack" });
    const modes = segmented([
        { value: "off", label: "Off" }, { value: "self", label: "Self-signed" }, { value: "cert", label: "My certificate" }, { value: "acme", label: "Let's Encrypt" },
    ], httpsMode, v => { httpsMode = v; paintHttps(); });

    function paintHttps() {
        clear(httpsDetails);
        if (httpsMode === "off") httpsDetails.append(h("div", { class: "callout info" }, icon("info"), h("span", { text: "Fine for this computer only. Turn HTTPS on before opening the panel to the internet, so passwords aren't sent in the clear." })));
        if (httpsMode === "self") httpsDetails.append(h("div", { class: "callout warn" }, icon("warn"), h("span", { text: "Encrypted, but browsers show a warning the first time because the certificate isn't from a trusted authority." })));
        if (httpsMode === "cert") httpsDetails.append(h("div", { class: "form-grid" }, field("Certificate file", certPath, { span: true, help: "A certificate you already have: a .pfx file, or a PEM pair (fullchain.pem + privkey.pem, e.g. from Certbot). Browsers trust it if it's for the address you open the panel with." }), field("Private key file", keyPath, { hint: "Only for PEM files with a separate key." }), field("Password", certPassword, { hint: "PFX or encrypted key password." })));
        if (httpsMode === "acme") httpsDetails.append(
            h("div", { class: "callout info" }, icon("globe"), h("span", { text: "Free, trusted certificate, renewed automatically. Needs a domain pointing at this machine's public IP, and port 80 forwarded to it." })),
            h("div", { class: "form-grid" }, field("Domain", domain, { help: ["A domain name (e.g. games.example.com) whose DNS points at your public IP. Let's Encrypt checks it by connecting to port 80, so forward port 80 to this PC on your router.", "The certificate renews itself before it expires."] }), field("Email for expiry notices", email, { help: "Let's Encrypt writes here only if renewal keeps failing, so you hear about it before the certificate expires." })), staging);
    }
    paintHttps();
    // The certificate in use now: the WindowsGSM app on another PC shows this fingerprint the first time it connects.
    const certInfo = cert?.https ? h("div", { class: "callout info" }, icon("lock"), h("span", {},
        `Serving ${cert.subject || "the panel"} until ${new Date(cert.expires).toLocaleDateString()}. Fingerprint (SHA-256): `,
        h("code", { class: "mono", style: { "word-break": "break-all" }, text: cert.fingerprint }),
        cert.trusted ? "" : " — the WindowsGSM app on another PC shows this the first time it connects; check they match.")) : null;

    const startupToggle = toggle("Start the agent when I sign in to Windows", startup.registered, {
        help: ["The agent runs your servers and this panel. With this on it starts when you sign in to Windows (and is started again if it ever stops), so servers with auto-start come back after a reboot.",
            "On a dedicated PC, also turn on Windows' automatic sign-in so it all comes back after a power cut without anyone signing in."],
        hint: "Restarts it automatically if it ever stops. Pair with Windows auto sign-in on a dedicated machine so servers come back after a reboot.",
        onChange: async on => {
            try { await post("/agent/startup", { enabled: on }); toast(on ? "The agent will start with Windows" : "Start with Windows turned off", { type: "good" }); }
            catch (e) { toastError(e, "Couldn't change that"); startupToggle.input.checked = !on; }
        },
    });

    const httpsHelp = helpButton("HTTPS", [
        "HTTPS encrypts everything between your browser and the panel — passwords included. Off is fine when the panel is only used on this PC.",
        "Self-signed: encrypted straight away, but browsers warn once. My certificate: one you already have. Let's Encrypt: free and trusted by every browser, renewed automatically — needs a domain and port 80 forwarded.",
        "Restart the agent after changing it."]);
    const restartNote = h("div", { class: "callout warn", hidden: true }, icon("restart"), h("span", { text: "Saved. Restart the agent for network and HTTPS changes to take effect." }));
    const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
        await put("/agent/settings", {
            machineId: cfg.machineId, machineName: machineName.value.trim(), port: Number(port.value), exposeToNetwork: network.input.checked,
            useHttps: httpsMode !== "off", certPath: httpsMode === "cert" ? certPath.value.trim() : "", keyPath: httpsMode === "cert" ? keyPath.value.trim() : "",
            certPassword: certPassword.value ? certPassword.value : null,
            acmeEnabled: httpsMode === "acme", acmeDomain: domain.value.trim(), acmeEmail: email.value.trim(), acmeStaging: staging.input.checked,
            sessionHours: Number(sessionHours.value),
        });
        restartNote.hidden = false;
        await store.refreshMachine();
        toast("Settings saved", { type: "good" });
    }, "Settings not saved") }, icon("save"), "Save settings");

    host.append(
        h("div", { class: "page-head" }, h("div", {}, h("h1", { text: "Agent settings" }), h("p", {}, "This machine's agent. Machine id ", h("code", { text: cfg.machineId }))),
            store.me.isOwner ? h("div", { class: "actions" }, restartButton()) : null),
        restartNote,
        h("div", { class: "settings-grid" },
            h("section", { class: "panel settings-section" }, h("div", { class: "panel-head" }, icon("machine"), h("h3", { text: "This machine" })),
                h("div", { class: "panel-body stack" }, field("Name", machineName, { hint: "How it appears in the panel.", help: "Shown in the machine picker, on the overview and in notifications — handy once you connect several PCs." }), startupToggle)),
            h("section", { class: "panel settings-section" }, h("div", { class: "panel-head" }, icon("globe"), h("h3", { text: "Network" })),
                h("div", { class: "panel-body stack" }, network, h("div", { class: "form-grid" }, field("Port", port, { hint: "Default 8971.", help: ["The port the panel listens on. Change it only if something else uses 8971.", "If you forward it on your router or allowed it through a firewall, change those too. Restart the agent afterwards."] }), field("Stay signed in for", sessionHours, { hint: "hours without activity", help: "How long a browser stays signed in without being used. Shorter is safer on shared or public computers; the desktop app on this PC can stay signed in regardless (its tray menu)." })))),
            h("section", { class: "panel settings-section" }, h("div", { class: "panel-head" }, icon("lock"), h("h3", { text: "HTTPS" }), httpsHelp),
                h("div", { class: "panel-body stack" }, httpsHelp.helpBox, modes, httpsDetails, certInfo)),
            steamPanel(store.localId),
            offsitePanel(store.localId),
            h("section", { class: "panel settings-section" }, h("div", { class: "panel-head" }, icon("update"), h("h3", { text: "Updates" })),
                h("div", { class: "panel-body" }, updatesPanel(store.localId, scope)))),
        h("div", { class: "row" }, h("span", { class: "spacer" }), save));
}

/** Restarts the agent (owners). Game servers keep running; the panel reconnects on its own. */
function restartButton() {
    const b = h("button", { class: "btn", title: "Game servers keep running", onclick: async () => {
        if (!(await confirm({ title: "Restart the agent?", message: "The panel goes away for a few seconds and comes back on its own. Game servers keep running.", confirmLabel: "Restart", iconName: "restart" }))) return;
        await busy(b, async () => {
            await post(`/machines/${encodeURIComponent(store.localId)}/agent/restart`);
            toast("Restarting the agent", { type: "info", text: "Back in a few seconds — your game servers keep running.", timeout: 8000 });
        }, "Couldn't restart the agent");
    } }, icon("restart"), "Restart agent");
    return b;
}
