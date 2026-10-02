// Steam account (owners): for games that can't be downloaded anonymously. Signing in answers Steam Guard here
// (email code or authenticator code), and the login is remembered for installs and updates. The password is stored
// encrypted on this machine.

import { h, icon, clear, append } from "../dom.js";
import { get, post, del } from "../api.js";
import { field, input, toast, toastError, busy, confirm } from "../ui.js";

export function steamPanel(machineId) {
    const base = `/machines/${encodeURIComponent(machineId)}/steam-account`;
    const body = h("div", { class: "panel-body stack" });
    let timer = null;

    async function load() {
        try { paint(await get(base)); }
        catch (e) { clear(body); body.append(h("div", { class: "callout warn" }, icon("warn"), h("span", { text: e.message || "Couldn't load the Steam account." }))); }
    }

    function paint(st) {
        clear(body);
        if (st.legacyPlainText) {
            const fix = h("button", { class: "btn sm", onclick: () => busy(fix, async () => { paint(await post(`${base}/remove-legacy-password`)); toast("Plain-text password removed", { type: "good" }); }, "Couldn't change the file") }, icon("lock"), "Remove plain-text copy");
            body.append(h("div", { class: "callout warn" }, icon("warn"),
                h("span", { text: "The old WindowsGSM file bin\\steamcmd\\userData.txt holds your Steam password as plain text. WindowsGSM keeps an encrypted copy, so the plain one can go (the old app then can't sign in to Steam by itself)." }), fix));
        }
        const sign = st.signIn || { state: "idle" };
        if (sign.state === "running" || sign.state === "code") { paintSignIn(sign); return; }

        if (st.username) {
            const when = st.signedInAt ? `signed in ${new Date(st.signedInAt).toLocaleDateString()}` : st.fromLegacyFile ? "from the old WindowsGSM file" : "not signed in yet";
            const remove = h("button", { class: "btn sm danger", onclick: async () => {
                if (!(await confirm({ title: "Remove the Steam account?", message: "Games that need an account won't install or update until you sign in again.", confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
                await busy(remove, async () => { paint(await del(base)); toast("Steam account removed", { type: "good" }); }, "Couldn't remove it");
            } }, icon("trash"), "Remove");
            body.append(h("div", { class: "row" }, icon("user"), h("strong", { text: st.username }), h("span", { class: "muted", text: when }), h("span", { class: "spacer" }), remove));
        } else {
            body.append(h("p", { class: "muted", text: "Most servers download anonymously. Some games (and some Workshop items) need a Steam account that owns them — sign in once here." }));
        }
        if (sign.state === "done") body.append(h("div", { class: "callout good" }, icon("check"), h("span", { text: sign.message })));
        if (sign.state === "failed") body.append(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: sign.message })));
        body.append(form(st));
    }

    function form(st) {
        const user = input({ value: st.username || "", autocomplete: "off", placeholder: "Steam sign-in name" });
        const pass = input({ type: "password", autocomplete: "new-password", placeholder: st.hasPassword ? "Unchanged" : "" });
        const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
            const sign = await post(base, { username: user.value.trim(), password: pass.value || null });
            pass.value = "";
            paintSignIn(sign);
        }, "Couldn't start signing in") }, icon("key"), st.username ? "Sign in again" : "Sign in");
        return h("div", { class: "stack" },
            h("div", { class: "form-grid" }, field("Username", user, { help: "Your Steam sign-in name (what you type at the Steam login), not your profile name. A separate Steam account just for servers is a good idea — it only needs to own the games." }), field("Password", pass, { hint: "Stored encrypted on this machine." })),
            h("div", { class: "row" }, h("span", { class: "muted small", text: "Consider a separate Steam account just for servers." }), h("span", { class: "spacer" }), go));
    }

    function paintSignIn(sign) {
        clear(body);
        const out = h("pre", { class: "console-mini", text: (sign.output || []).join("\n") });
        const cancel = h("button", { class: "btn sm", onclick: () => busy(cancel, async () => { await post(`${base}/signin/cancel`); await load(); }) }, "Cancel");
        if (sign.state === "code") {
            const code = input({ autocomplete: "one-time-code", inputmode: "text", class: "input mono", placeholder: "ABCDE", maxlength: 16 });
            const send = h("button", { class: "btn primary", onclick: () => busy(send, async () => { paintSignIn(await post(`${base}/signin/code`, { code: code.value.trim() })); }, "Code not sent") }, icon("check"), "Send code");
            code.addEventListener("keydown", e => { if (e.key === "Enter") send.click(); });
            body.append(h("div", { class: "callout info" }, icon("lock"), h("span", { text: sign.prompt })),
                h("div", { class: "row" }, field("Steam Guard code", code, { help: "The 5-character code from the Steam Mobile App (Steam Guard) or the email Steam just sent. It's only needed once — the sign-in is remembered afterwards." }), h("span", { class: "spacer" }), cancel, send));
            setTimeout(() => code.focus(), 0);
        } else {
            append(body, h("div", { class: "row" }, h("span", { class: "spinner" }), h("span", { text: `Signing in as ${sign.username}… If Steam asks you to approve it in the Steam Mobile App, do that now.` }), h("span", { class: "spacer" }), cancel));
        }
        body.append(out);
        poll(sign.state);
    }

    function poll(state) {
        clearTimeout(timer);
        if (!body.isConnected && timer !== null) return;
        // While waiting for a code, nothing changes until it's sent — poll slowly; otherwise follow along.
        timer = setTimeout(async () => {
            if (!body.isConnected) return;
            try {
                const sign = await get(`${base}/signin`);
                if (sign.state === "running" || (sign.state === "code" && state !== "code")) paintSignIn(sign);
                else if (sign.state === "code") poll("code");
                else load();
            } catch (e) { toastError(e, "Lost track of the sign-in"); }
        }, state === "code" ? 4000 : 1500);
    }

    load();
    return h("section", { class: "panel settings-section" }, h("div", { class: "panel-head" }, icon("steam"), h("h3", { text: "Steam account" })), body);
}
