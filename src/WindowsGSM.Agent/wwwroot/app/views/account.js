// Your account: password, two-factor sign-in (with a QR code), and where you're signed in.

import { h, icon, clear, timeAgo, fmtDateTime, copyText } from "../dom.js";
import { get, post } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { field, input, toast, toastError, busy, confirm, modal, empty } from "../ui.js";
import { qrSvg } from "../qr.js";
import { applyTheme, currentTheme, applyAccent, currentAccent, ACCENTS } from "../theme.js";
import { getPref, setPref } from "../prefs.js";
import { tipsOn, resetTips } from "../tips.js";
import { segmented } from "../ui.js";
import { del } from "../api.js";
import { passkeysAvailable, addPasskey, deviceName } from "../passkeys.js";

export default async function account(host, { scope }) {
    setCrumbs({ label: "Your account" });
    const me = store.me;
    const twoFactor = h("div", { class: "stack" });
    const sessions = h("div");
    const passkeys = h("div", { class: "stack" });

    host.append(
        h("div", { class: "page-head" }, h("span", { class: "avatar lg", text: me.username.slice(0, 1) }),
            h("div", {}, h("h1", { text: me.username }), h("p", { text: `${me.role} on ${store.machine.name}` }))),
        h("div", { class: "account-grid" },
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, icon("shield"), h("h2", { text: "Two-factor sign-in" })), h("div", { class: "panel-body" }, twoFactor)),
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, icon("key"), h("h2", { text: "Password" })), h("div", { class: "panel-body" }, passwordForm())),
            h("section", { class: "panel span-2" }, h("div", { class: "panel-head" }, icon("shield"), h("h2", { text: "Passkeys" }),
                h("span", { class: "sub", text: "Sign in with your fingerprint, face or phone — no password or code." })), h("div", { class: "panel-body" }, passkeys)),
            h("section", { class: "panel span-2" },
                h("div", { class: "panel-head" }, icon("monitor"), h("h2", { text: "Where you're signed in" }), h("span", { class: "spacer" }),
                    h("button", { class: "btn sm", onclick: e => busy(e.currentTarget, async () => {
                        const r = await post("/auth/sessions/revoke-others");
                        toast(r.revoked ? `Signed out ${r.revoked} other session${r.revoked === 1 ? "" : "s"}` : "No other sessions", { type: "good" });
                        loadSessions();
                    }) }, icon("logout"), "Sign out everywhere else")),
                h("div", { class: "panel-body flush" }, sessions)),
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, icon("sun"), h("h2", { text: "Appearance" }), h("span", { class: "sub", text: "This browser only." })),
                h("div", { class: "panel-body" }, appearance()))));

    function appearance() {
        const swatchColor = { blue: "#3b82f6", violet: "#8b5cf6", teal: "#14b8a6", orange: "#f97316", rose: "#f43f5e" };
        const swatches = h("div", { class: "swatches", role: "group", "aria-label": "Accent colour" });
        const paintSwatches = () => clear(swatches).append(...ACCENTS.map(a => {
            const b = h("button", { type: "button", class: "swatch", title: a[0].toUpperCase() + a.slice(1), "aria-label": a, "aria-pressed": String(currentAccent() === a), onclick: () => { applyAccent(a); paintSwatches(); } });
            b.style.setProperty("background", swatchColor[a]);
            return b;
        }));
        paintSwatches();
        const row = (label, control) => h("div", {}, h("div", { class: "label", text: label }), control);
        const wrap = h("input", { type: "checkbox", checked: getPref("consoleWrap") });
        wrap.addEventListener("change", () => setPref("consoleWrap", wrap.checked));
        return h("div", { class: "appearance" },
            row("Theme", segmented([{ value: "dark", label: "Dark", icon: "moon" }, { value: "light", label: "Light", icon: "sun" }, { value: "system", label: "Match system", icon: "monitor" }],
                document.documentElement.dataset.theme || "system", v => applyTheme(v === "system" ? null : v))),
            row("Accent colour", swatches),
            row("Server list", segmented([{ value: "comfortable", label: "Comfortable" }, { value: "compact", label: "Compact" }], getPref("density"), v => setPref("density", v))),
            row("Console text", h("div", { class: "row wrap" },
                segmented([{ value: "small", label: "Small" }, { value: "medium", label: "Medium" }, { value: "large", label: "Large" }], getPref("consoleSize"), v => setPref("consoleSize", v)),
                h("label", { class: "row small checkline" }, wrap, "Wrap long lines"))),
            row("Tips", h("div", { class: "row wrap" },
                segmented([{ value: "on", label: "Show tips" }, { value: "off", label: "Hide tips" }], tipsOn() ? "on" : "off", v => setPref("tips", v === "on")),
                h("button", { type: "button", class: "btn ghost sm", title: "Bring back every tip you dismissed", onclick: () => { resetTips(); toast("Tips are back", { type: "good", text: "Each page shows its tip again." }); } }, icon("restart"), "Show dismissed tips again"))));
    }

    // ── Passkeys ──
    async function loadPasskeys() {
        let list;
        try { list = await get("/auth/passkeys"); } catch (e) { clear(passkeys).append(h("div", { class: "small faint", text: e.message })); return; }
        clear(passkeys);
        for (const k of list) {
            passkeys.append(h("div", { class: "list-item passkey-row" }, icon("key"),
                h("div", { class: "grow" }, h("b", { text: k.name }),
                    h("div", { class: "small muted", text: `For ${k.rpId} · added ${timeAgo(k.createdAt)}${k.lastUsedAt ? " · last used " + timeAgo(k.lastUsedAt) : " · not used yet"}` })),
                h("button", { class: "btn ghost sm danger-text", onclick: async () => {
                    if (!(await confirm({ title: `Remove “${k.name}”?`, message: "That device can't sign in with it any more (remove it from the device's passkey list too).", confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
                    try { await del(`/auth/passkeys/${encodeURIComponent(k.id)}`); loadPasskeys(); } catch (e) { toastError(e); }
                } }, icon("trash"), "Remove")));
        }
        if (!passkeysAvailable()) {
            passkeys.append(h("div", { class: "callout info" }, icon("info"), h("span", { text: "Passkeys need the panel's HTTPS address (browsers don't allow them on plain http, except on this computer's localhost). Open it over HTTPS to add one." })));
            return;
        }
        const name = h("input", { class: "input", value: deviceName(), maxlength: 60, "aria-label": "Passkey name" });
        const add = h("button", { class: "btn primary", onclick: () => busy(add, async () => {
            try { await addPasskey(name.value.trim()); }
            catch (e) { if (e.name === "NotAllowedError" || e.name === "AbortError") return; throw e; }
            toast("Passkey added", { type: "good", text: `Next time, choose “Sign in with a passkey” on ${location.host}.` });
            loadPasskeys();
        }, "Couldn't add the passkey") }, icon("plus"), "Add a passkey");
        passkeys.append(h("div", { class: "row wrap" }, h("div", { class: "grow" }, name), add),
            h("div", { class: "tiny faint", text: `A passkey works on the address it was made on (${location.host}). Your phone or computer keeps it — WindowsGSM only stores its public half.` }));
    }
    loadPasskeys();

    function passwordForm() {
        const current = input({ type: "password", autocomplete: "current-password" });
        const next = input({ type: "password", autocomplete: "new-password" });
        const again = input({ type: "password", autocomplete: "new-password" });
        const nextField = field("New password", next, { hint: "At least 8 characters." });
        const againField = field("Confirm new password", again);
        const btn = h("button", { class: "btn primary", type: "submit" }, "Change password");
        const form = h("form", { class: "stack" }, field("Current password", current), nextField, againField,
            h("p", { class: "tiny faint", text: "Changing your password signs you out on every other device." }), h("div", { class: "row" }, btn));
        form.addEventListener("submit", async e => {
            e.preventDefault();
            nextField.setError(""); againField.setError("");
            if (next.value.length < 8) { nextField.setError("Use at least 8 characters."); return; }
            if (next.value !== again.value) { againField.setError("The passwords don't match."); return; }
            await busy(btn, async () => {
                await post("/auth/password", { currentPassword: current.value, newPassword: next.value });
                form.reset();
                toast("Password changed", { type: "good", text: "Other devices have been signed out." });
                loadSessions();
            }, "Password not changed");
        });
        return form;
    }

    function paintTwoFactor() {
        clear(twoFactor);
        if (store.me.twoFactorEnabled) {
            const code = input({ class: "input mono", inputmode: "numeric", placeholder: "123456", maxlength: 8 });
            twoFactor.append(
                h("div", { class: "callout good" }, icon("checkCircle"), h("span", { text: "On. Signing in asks for a code from your authenticator app." })),
                field("To turn it off, enter a current code", code),
                h("div", { class: "row" }, h("button", { class: "btn danger", onclick: e => busy(e.currentTarget, async () => {
                    await post("/auth/2fa/disable", { code: code.value.replace(/\s/g, "") });
                    store.me = await get("/auth/me");
                    toast("Two-factor sign-in is off", { type: "warn" });
                    paintTwoFactor();
                }, "Couldn't turn it off") }, "Turn off")));
        } else {
            twoFactor.append(
                h("p", { class: "muted", text: "Adds a 6-digit code from an app like Google Authenticator, Microsoft Authenticator or Authy to your sign-in — so a leaked password isn't enough." }),
                h("div", { class: "row" }, h("button", { class: "btn primary", onclick: e => busy(e.currentTarget, enrol, "Couldn't start setup") }, icon("shield"), "Set up two-factor")));
        }
    }

    async function enrol() {
        const setup = await post("/auth/2fa/setup");
        const code = input({ class: "input mono code-input", inputmode: "numeric", placeholder: "123456", maxlength: 8, autofocus: true });
        const codeField = field("Enter the 6-digit code it shows", code);
        const qr = h("div", { class: "qr" });
        qr.append(qrSvg(setup.uri));
        modal({
            title: "Set up two-factor sign-in", iconName: "shield", tone: "accent", wide: true,
            subtitle: "Scan this with your authenticator app, then type the code it shows.",
            body: h("div", { class: "enrol" }, qr,
                h("div", { class: "stack" },
                    h("div", { class: "small muted", text: "Can't scan? Enter this key instead:" }),
                    h("button", { class: "secret mono", title: "Copy", onclick: async () => { if (await copyText(setup.secret)) toast("Key copied", { type: "good", timeout: 2000 }); } }, setup.secret.replace(/(.{4})/g, "$1 ").trim(), icon("copy")),
                    codeField)),
            footer: close => {
                const ok = h("button", { class: "btn primary", onclick: () => busy(ok, async () => {
                    try { await post("/auth/2fa/enable", { code: code.value.replace(/\s/g, "") }); }
                    catch (e) { codeField.setError(e.message); code.select(); return; }
                    store.me = await get("/auth/me");
                    close(true);
                    toast("Two-factor sign-in is on", { type: "good", text: "You'll be asked for a code next time you sign in." });
                    paintTwoFactor();
                }) }, "Turn on");
                code.addEventListener("keydown", e => { if (e.key === "Enter") ok.click(); });
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), ok];
            },
        });
    }

    async function loadSessions() {
        const list = await get("/auth/sessions");
        clear(sessions);
        if (!list.length) { sessions.append(empty("monitor", "No sessions", "")); return; }
        for (const s of list) {
            // "app-…": a WindowsGSM app on another PC that stays signed in (removing it makes that app sign in again).
            const app = s.id.startsWith("app-");
            sessions.append(h("div", { class: "list-item" },
                h("span", { class: ["session-icon", s.current && "current"] }, icon(app ? "key" : "monitor")),
                h("div", { class: "grow" },
                    h("b", { text: s.device || "Unknown device" }), s.current ? h("span", { class: "tag accent", text: "This device" }) : null,
                    h("div", { class: "small muted", text: app
                        ? `${s.ip || "unknown address"} · last signed in ${timeAgo(s.lastSeenAt)} · remembered since ${fmtDateTime(s.createdAt)}`
                        : `${s.ip || "unknown address"} · active ${timeAgo(s.lastSeenAt)} · signed in ${fmtDateTime(s.createdAt)}` })),
                s.current ? null : h("button", { class: "btn ghost sm", title: app ? "That app signs in with a password again next time" : null,
                    onclick: e => busy(e.currentTarget, async () => { await post(`/auth/sessions/${s.id}/revoke`); loadSessions(); }) }, app ? "Remove" : "Sign out")));
        }
    }

    paintTwoFactor();
    await loadSessions();
}
