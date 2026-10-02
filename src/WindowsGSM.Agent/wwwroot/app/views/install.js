// Install wizard: 1 pick a machine (when there are several) and a game → 2 name it (branch, agreements) →
// 3 watch it install, live.

import { h, icon, clear, append, gameLabel, gameTile, debounce } from "../dom.js";
import { get, post } from "../api.js";
import { store, key } from "../store.js";
import { setCrumbs } from "../shell.js";
import { navigate, serverPath, setLeaveGuard } from "../router.js";
import { field, input, select, segmented, empty, loading, toastError, progressBar, setProgress, busy, confirm } from "../ui.js";
import { runAction } from "../actions.js";
import { installMachines } from "../perms.js";
import { machinePicker } from "./machines.js";

const CONSENT_TEXT = {
    eula: { title: "Accept the game's licence agreement (EULA)", text: "The game asks you to agree to its end-user licence before it can be installed." },
    "install-java": { title: "Install Java if it's missing", text: "Minecraft: Java Edition needs Java. If it isn't on this machine, it's downloaded and installed for you." },
};

export default async function install(host, { scope, query }) {
    setCrumbs({ label: "Overview", href: "/" }, { label: "Install a server" });
    const targets = installMachines();
    let machine = targets.some(m => m.id === query.get("machine")) ? query.get("machine") : (targets[0]?.id || store.localId);
    const onMachine = rest => `/machines/${encodeURIComponent(machine)}${rest}`;
    const where = () => store.multiMachine ? ` on ${store.machineName(machine)}` : "";

    const stepper = h("ol", { class: "stepper" });
    const body = h("div", { class: "wizard-body" });
    host.append(h("div", { class: "page-head" }, h("div", {},
        h("h1", { text: "Install a server" }),
        h("p", { text: "Pick a game, give it a name — WindowsGSM downloads it, sets it up and chooses free ports." }))),
        stepper, body);

    function paintSteps(active) {
        clear(stepper).append(...["Choose a game", "Name & options", "Install"].map((label, i) =>
            h("li", { class: [i < active && "done", i === active && "active"] }, h("span", { class: "step-num" }, i < active ? icon("check") : String(i + 1)), h("span", { text: label }))));
    }

    // ── Step 1 ──
    async function chooseGame() {
        paintSteps(0);
        clear(body).append(loading("Loading games…"));
        let games;
        const picker = targets.length > 1
            ? machinePicker(machine, m => { machine = m; history.replaceState({}, "", "/install?machine=" + encodeURIComponent(m)); chooseGame(); }, { only: targets.map(m => m.id), label: "Install on" })
            : null;
        try { games = await store.loadGames(machine); } catch (e) { clear(body).append(picker || "", empty("warn", "Couldn't load the game list", e.message)); return; }
        const preselect = query.get("game");
        if (preselect) { const g = games.find(x => x.name === preselect); if (g) return nameIt(g); }

        let kind = "all";
        let search = "";
        const box = h("input", { class: "input", type: "search", placeholder: `Search ${games.length} games…`, "aria-label": "Search games", autofocus: true });
        const grid = h("div", { class: "game-grid" });
        const filters = segmented([
            { value: "all", label: "All", count: games.length },
            { value: "steam", label: "Steam", count: games.filter(g => g.isSteam).length },
            { value: "plugin", label: "Plugins", count: games.filter(g => g.isPlugin).length },
        ], kind, v => { kind = v; paint(); });
        box.addEventListener("input", debounce(() => { search = box.value.trim().toLowerCase(); paint(); }, 100));

        function paint() {
            const list = games.filter(g => (kind === "all" || (kind === "steam" ? g.isSteam : g.isPlugin)) && (!search || g.name.toLowerCase().includes(search)));
            clear(grid);
            if (!list.length) { grid.append(empty("search", "No games match", "Community plugins add more games — drop a plugin into the plugins folder.")); return; }
            for (const g of list) {
                grid.append(h("button", { class: "game-card", onclick: () => nameIt(g) },
                    gameTile(g.name, g.iconUrl, "", g.artUrl),
                    h("span", { class: "grow" },
                        h("b", { text: gameLabel(g.name) }),
                        h("span", { class: "game-tags" },
                            g.isSteam ? h("span", { class: "tag cyan" }, icon("steam"), "Steam") : null,
                            g.isPlugin ? h("span", { class: "tag violet" }, icon("puzzle"), "Plugin") : null,
                            g.consents.length ? h("span", { class: "tag amber", title: "Asks you to accept an agreement" }, "EULA") : null)),
                    icon("chevronRight")));
            }
        }
        const more = store.me.canManageUsers
            ? h("p", { class: "small muted install-more" }, icon("puzzle"), " Don't see your game? ", h("a", { href: "/plugins?machine=" + encodeURIComponent(machine) }, "Browse community plugins"), ".")
            : null;
        append(clear(body), [h("div", { class: "toolbar" }, picker, h("div", { class: "search grow" }, icon("search"), box), filters), grid, more]);
        paint();
        box.focus();
    }

    // ── Step 2 ──
    function nameIt(game) {
        paintSteps(1);
        const name = input({ value: `${gameLabel(game.name).replace(/ Dedicated Server| Server$/i, "")} Server`, maxlength: 100, autofocus: true });
        const nameField = field("Server name", name, { hint: "Shown in the panel and, for most games, in the server browser.", help: "You can change it any time in the server's Settings. Some games take the in-game name from their own config file instead (Game config)." });
        const branch = select([{ value: "", label: "public (default)" }], "");
        const branchPw = input({ type: "password", autocomplete: "off" });
        const branchPwField = field("Branch password", branchPw, { help: "This branch is password-protected. The game's developers publish the password (often for modders or testers)." });
        branchPwField.hidden = true;
        const branchField = field("Steam branch", branch, { hint: "Loading branches from Steam…", help: ["Most servers use the public (default) branch. Others are beta or older versions the developers publish — e.g. an experimental update, or an old version for a mod that isn't updated yet.", "Players need the same branch to join. You can change it later in Settings."] });
        const consents = game.consents.map(key => {
            const box = h("input", { type: "checkbox" });
            const c = CONSENT_TEXT[key] || { title: key, text: "" };
            const el = h("label", { class: "consent" }, box, h("span", {}, h("b", { text: c.title }), h("small", { text: c.text })));
            el.key = key;
            el.box = box;
            return el;
        });
        // Server templates for this game: settings and config files saved from another server.
        const template = select([{ value: "", label: "None — start fresh" }], "");
        const templateField = field("Start from a template", template, { hint: "Copies the template's settings and config files once it's installed (with this server's own ports).", help: "Templates are saved from another server of this game (its ⋯ menu → Save as template) — handy for running several servers set up the same way." });
        templateField.hidden = true;
        get(onMachine("/templates")).then(list => {
            const mine = list.filter(t => t.game === game.name);
            for (const t of mine) template.append(h("option", { value: t.id }, t.name + (t.description ? ` — ${t.description}` : "")));
            templateField.hidden = mine.length === 0;
            const wanted = query.get("template");
            if (wanted && mine.some(t => t.id === wanted)) template.value = wanted;
        }).catch(() => { /* older agent or no access: no templates */ });
        const err = h("div", { class: "callout bad", hidden: true, role: "alert" }, icon("warn"), h("span"));
        const go = h("button", { class: "btn primary lg", onclick: () => start() }, icon("download"), "Install");

        clear(body).append(h("div", { class: "wizard-card card pad" },
            h("div", { class: "row" }, gameTile(game.name, game.iconUrl, "lg", game.artUrl),
                h("div", { class: "grow" }, h("h2", { text: gameLabel(game.name) }),
                    h("div", { class: "small muted", text: [store.multiMachine ? `On ${store.machineName(machine)}` : null, game.isSteam ? `Steam app ${game.appId} · downloaded with DepotDownloader` : "Downloaded by the game's plugin", game.author ? `by ${game.author}` : null].filter(Boolean).join(" · ") }),
                    game.description ? h("p", { class: "small faint", text: game.description }) : null),
                h("button", { class: "btn ghost sm", onclick: () => chooseGame() }, "Change")),
            err,
            h("div", { class: "form-grid" }, nameField, templateField, game.isSteam ? branchField : null, game.isSteam ? branchPwField : null),
            consents.length ? h("div", { class: "stack tight" }, h("span", { class: "upper", text: "Agreements" }), ...consents,
                h("p", { class: "tiny faint", text: "Unticked questions are asked while installing instead — you can answer them from the activity panel." })) : null,
            h("div", { class: "callout info" }, icon("sparkles"), h("span", { text: "Ports are chosen automatically so this server won't clash with your others. You can change them in the server's settings." })),
            h("div", { class: "row" }, h("span", { class: "spacer" }), h("button", { class: "btn", onclick: () => chooseGame() }, "Back"), go)));
        name.focus();
        name.select();

        if (game.isSteam) {
            get(onMachine(`/games/branches?game=${encodeURIComponent(game.name)}`)).then(list => {
                clear(branch);
                for (const b of list) branch.append(h("option", { value: b.name === "public" ? "" : b.name, dataset: { pwd: b.passwordRequired ? "1" : "" } },
                    b.name + (b.passwordRequired ? " (password)" : "") + (b.description ? ` — ${b.description}` : "")));
                branchField.querySelector(".hint").textContent = "Beta branches get new versions early.";
            }).catch(() => { branchField.querySelector(".hint").textContent = "Couldn't reach Steam for the branch list — the default branch will be used."; });
            branch.addEventListener("change", () => { branchPwField.hidden = !branch.selectedOptions[0]?.dataset.pwd; });
        }

        async function start() {
            if (!name.value.trim()) { nameField.setError("Give the server a name."); name.focus(); return; }
            await busy(go, async () => {
                try {
                    const res = await post(onMachine("/servers"), {
                        game: game.name, name: name.value.trim(),
                        steamBranch: branch.value || null, steamBranchPassword: branchPw.value || null,
                        consents: consents.filter(c => c.box.checked).map(c => c.key),
                        template: template.value || null,
                    });
                    store.trackJob(res.job, machine);
                    watch(game, { ...res.job, machine });
                } catch (e) { err.lastChild.textContent = e.message; err.hidden = false; }
            });
        }
    }

    // ── Step 3 ──
    function watch(game, job) {
        paintSteps(2);
        const title = h("h2", { text: `Installing ${gameLabel(game.name)}${where()}…` });
        const jobKey = key(machine, job.id);
        const stage = h("div", { class: "muted small", text: "Starting…" });
        const bar = progressBar(null);
        const pct = h("span", { class: "num install-pct", text: "" });
        const log = h("pre", { class: "job-log install-log" });
        const question = h("div");
        const actions = h("div", { class: "row" });
        const tile = gameTile(game.name, game.iconUrl, "lg", game.artUrl);
        const card = h("div", { class: "wizard-card card pad install-watch" },
            h("div", { class: "row" }, h("div", { class: "install-tile" }, tile), h("div", { class: "grow" }, title, stage), pct),
            bar, question, log, actions);
        clear(body).append(card);
        setLeaveGuard(null);

        function paint(j) {
            if (!j) return;
            stage.textContent = j.status === "Running" ? (j.stage || "Working…") : j.status === "Succeeded" ? "Installed and ready." : j.error || j.status;
            pct.textContent = j.percent != null ? j.percent + "%" : "";
            setProgress(bar, j.status === "Running" ? j.percent : 100, j.status === "Running" ? "" : j.status === "Succeeded" ? "done" : "failed");
            log.textContent = (j.recentLog || []).join("\n");
            log.scrollTop = log.scrollHeight;
            card.classList.toggle("done", j.status === "Succeeded");
            card.classList.toggle("failed", j.status === "Failed" || j.status === "Cancelled");
            clear(actions);
            if (j.status === "Running") {
                actions.append(h("span", { class: "spacer" }), h("button", { class: "btn ghost", onclick: async () => {
                    if (await confirm({ title: "Cancel the install?", message: "Anything downloaded so far is removed.", confirmLabel: "Cancel install", danger: true })) post(onMachine(`/jobs/${j.id}/cancel`)).catch(toastError);
                } }, "Cancel install"));
                return;
            }
            if (j.status === "Succeeded") {
                title.textContent = `${gameLabel(game.name)} is installed${where()}`;
                const server = j.serverId ? store.server(machine, j.serverId) : null; // the install job carries the new server's id
                append(actions, [
                    h("button", { class: "btn", onclick: () => { history.replaceState({}, "", "/install"); chooseGame(); } }, icon("plus"), "Install another"),
                    h("span", { class: "spacer" }),
                    server ? h("a", { class: "btn", href: serverPath(server.machine, server.id, "settings") }, icon("sliders"), "Review settings") : null,
                    server ? h("button", { class: "btn primary", onclick: async e => { e.currentTarget.classList.add("busy"); await runAction(server, "start").catch(() => { }); navigate(serverPath(server.machine, server.id, "console")); } }, icon("play"), "Start it now") : null]);
            } else {
                title.textContent = "The install didn't finish";
                actions.append(h("span", { class: "spacer" }), h("button", { class: "btn", onclick: () => navigate("/") }, "Back to overview"), h("button", { class: "btn primary", onclick: () => nameIt(game) }, icon("refresh"), "Try again"));
            }
        }

        function paintQuestion() {
            clear(question);
            const p = [...store.prompts.values()].find(x => x.machine === machine && x.jobId === job.id);
            if (!p) return;
            question.append(h("div", { class: "prompt-card inline" },
                h("h3", {}, icon("help"), p.title),
                h("p", { text: p.message }),
                h("div", { class: "row" },
                    h("button", { class: "btn primary", onclick: e => busy(e.currentTarget, () => post(onMachine(`/prompts/${p.id}`), { answer: true })) }, icon("check"), "Yes, continue"),
                    h("button", { class: "btn", onclick: e => busy(e.currentTarget, () => post(onMachine(`/prompts/${p.id}`), { answer: false })) }, "No"))));
        }

        scope.add(store.on("job:" + jobKey, paint));
        scope.add(store.on("prompts", paintQuestion));
        // The new server can show up a moment after the job finishes — then offer "Start it now".
        scope.add(store.on("servers", () => { const j = store.jobs.get(jobKey); if (j && j.status === "Succeeded") paint(j); }));
        paint(store.jobs.get(jobKey) || job);
        paintQuestion();
    }

    await chooseGame();
}
