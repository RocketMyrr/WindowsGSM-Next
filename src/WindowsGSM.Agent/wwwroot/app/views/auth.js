// Sign-in (with the two-factor step) and first-run setup, on the split brand screen.

import { h, icon, clear } from "../dom.js";
import { passkeysAvailable, signInWithPasskey } from "../passkeys.js";
import { get, post } from "../api.js";
import { navigate } from "../router.js";
import { field, input, busy } from "../ui.js";

function frame(root, card) {
    clear(root).append(h("div", { class: "auth" },
        h("section", { class: "auth-art", "aria-hidden": "true" },
            h("div", { class: "grid-lines" }),
            h("div", { class: "brand" }, h("span", { class: "brand-mark" }, h("img", { src: "/img/logo-96.png", alt: "", width: 38, height: 38 })), h("span", { class: "brand-name" }, "Windows", h("span", { text: "GSM" }))),
            h("div", {},
                h("h1", {}, "Every game server. ", h("em", { text: "One panel." })),
                h("p", { text: "Start, update, back up and watch all your servers — live, from any browser, on every machine you run." })),
            h("div", { class: "features" },
                ...[["zap", "Live console & stats"], ["update", "DepotDownloader updates"], ["backup", "Safe backups"], ["shield", "Roles & 2FA"], ["machine", "Multi-machine ready"]]
                    .map(([i, t]) => h("span", {}, icon(i), t)))),
        // The brand side is decoration (hidden from screen readers); the form is the page.
        h("main", { class: "auth-form" }, card)));
}

function errorBox() {
    const el = h("div", { class: "callout bad", role: "alert", hidden: true }, icon("warn"), h("span"));
    el.show = msg => { el.lastChild.textContent = msg; el.hidden = !msg; };
    return el;
}

export async function login(root, { query }) {
    let info = null;
    try { info = await get("/info"); } catch { /* shown on submit */ }
    if (info && info.setupRequired) { navigate("/setup", { replace: true }); return; }
    document.title = "Sign in · WindowsGSM";

    // In the desktop app on the server itself: it can sign back in on its own (as whoever last signed in there).
    const next = query.get("next");
    const goNext = () => location.assign(next && next.startsWith("/") && !next.startsWith("//") ? next : "/");
    if (window.chrome?.webview && !query.has("manual")) {
        frame(root, h("div", { class: "auth-card" }, h("div", { class: "empty" }, h("div", { class: "spinner lg" }), h("p", { text: "Signing you back in…" }))));
        const answer = await new Promise(resolve => {
            const onMessage = e => { if (e.data?.type === "signin-result") { window.chrome.webview.removeEventListener("message", onMessage); resolve(e.data); } };
            window.chrome.webview.addEventListener("message", onMessage);
            window.chrome.webview.postMessage({ type: "auto-signin" });
            setTimeout(() => resolve({ ok: false }), 6000);
        });
        if (answer.ok) { goNext(); return; }
    }

    const username = input({ autocomplete: "username", required: true, autofocus: true });
    const password = input({ type: "password", autocomplete: "current-password", required: true });
    const code = input({ inputmode: "numeric", autocomplete: "one-time-code", maxlength: 8, placeholder: "123 456", class: "input mono" });
    const codeField = field("Two-factor code", code, { hint: "From your authenticator app." });
    codeField.hidden = true;
    const err = errorBox();
    const submit = h("button", { class: "btn primary lg block", type: "submit" }, "Sign in", icon("chevronRight"));
    const passkeyBtn = passkeysAvailable() ? h("button", { class: "btn lg block", type: "button", onclick: () => busy(passkeyBtn, async () => {
        err.show("");
        try { await signInWithPasskey(); goNext(); }
        catch (ex) { if (ex.name !== "NotAllowedError" && ex.name !== "AbortError") err.show(ex.message); }
    }) }, icon("key"), "Sign in with a passkey") : null;

    const form = h("form", { class: "auth-card", novalidate: true },
        h("div", {}, h("h1", { text: "Welcome back" }), h("p", { class: "lead", text: info ? `Sign in to ${info.machineName}.` : "Sign in to continue." })),
        err,
        field("Username", username),
        field("Password", password),
        codeField,
        submit,
        passkeyBtn,
        h("p", { class: "tiny faint", text: "Signed-in sessions last a few hours without activity. Your sessions can be reviewed and signed out from Account & security." }));

    form.addEventListener("submit", async e => {
        e.preventDefault();
        err.show("");
        await busy(submit, async () => {
            try {
                const res = await post("/auth/login", { username: username.value.trim(), password: password.value, code: code.value.replace(/\s/g, "") || null });
                if (res.twoFactorRequired) {
                    codeField.hidden = false;
                    code.focus();
                    err.show("");
                    return;
                }
                goNext();
            } catch (ex) {
                err.show(ex.message);
                if (ex.code === "bad_code") { code.select(); } else { password.select(); }
            }
        });
    });
    frame(root, form);
}

export async function setup(root) {
    const info = await get("/info");
    if (!info.setupRequired) { navigate("/login", { replace: true }); return; }
    document.title = "Set up · WindowsGSM";

    const steps = h("div", { class: "steps", role: "img", "aria-label": "Step 1 of 2" }, h("span", { class: "done" }), h("span"));
    const username = input({ autocomplete: "username", value: "", autofocus: true });
    const password = input({ type: "password", autocomplete: "new-password" });
    const confirmPw = input({ type: "password", autocomplete: "new-password" });
    const machineName = input({ value: info.machineName });
    const token = input({ class: "input mono", placeholder: "8-character code", maxlength: 8 });
    const tokenField = field("Setup code", token, { hint: "Shown in the agent's window and log. Only needed when you're not on the machine itself." });
    tokenField.hidden = true;
    const err = errorBox();
    const submit = h("button", { class: "btn primary lg block", type: "submit" }, "Create owner account", icon("chevronRight"));
    const pwField = field("Password", password, { hint: "At least 8 characters. A passphrase is easiest to remember." });
    const confirmField = field("Confirm password", confirmPw);

    const form = h("form", { class: "auth-card", novalidate: true },
        steps,
        h("div", {}, h("h1", { text: "Set up WindowsGSM" }), h("p", { class: "lead", text: "Create the owner account. The owner can manage everything, including other people's access." })),
        err,
        field("Your name", username, { hint: "What you'll sign in with." }),
        pwField,
        confirmField,
        field("Machine name", machineName, { hint: "How this computer appears in the panel — handy once you add more machines." }),
        tokenField,
        submit);

    form.addEventListener("submit", async e => {
        e.preventDefault();
        err.show("");
        pwField.setError("");
        confirmField.setError("");
        if (password.value.length < 8) { pwField.setError("Use at least 8 characters."); password.focus(); return; }
        if (password.value !== confirmPw.value) { confirmField.setError("The passwords don't match."); confirmPw.focus(); return; }
        await busy(submit, async () => {
            try {
                await post("/setup", { username: username.value.trim(), password: password.value, machineName: machineName.value.trim(), token: token.value.trim() || null });
                location.assign("/?welcome=1");
            } catch (ex) {
                if (ex.status === 403) { tokenField.hidden = false; token.focus(); }
                err.show(ex.message);
            }
        });
    });
    frame(root, form);
}
