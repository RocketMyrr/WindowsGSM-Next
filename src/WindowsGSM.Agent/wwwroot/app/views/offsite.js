// Agent settings → Off-site backups: one S3-compatible bucket for this machine (Backblaze B2, Cloudflare R2,
// Wasabi, Amazon S3, or anything else that speaks S3). Each server then chooses "Also upload each backup
// off-site" on its Backups tab.

import { h, icon, clear } from "../dom.js";
import { get, put, post } from "../api.js";
import { field, input, toggle, toast, busy, select } from "../ui.js";

const PROVIDERS = [
    { value: "b2", label: "Backblaze B2", endpoint: "https://s3.us-west-004.backblazeb2.com", region: "us-west-004",
      hint: "Bucket page → Endpoint (add https://). Region is the part after “s3.”. Keys: App Keys → Add a New Application Key (that bucket, read and write)." },
    { value: "r2", label: "Cloudflare R2", endpoint: "https://<account id>.r2.cloudflarestorage.com", region: "auto",
      hint: "R2 → your bucket → Settings → S3 API. Keys: R2 → Manage API tokens → Create (Object Read & Write). Region: auto." },
    { value: "wasabi", label: "Wasabi", endpoint: "https://s3.us-east-1.wasabisys.com", region: "us-east-1",
      hint: "The endpoint for your bucket's region (s3.<region>.wasabisys.com). Keys: Access Keys → Create New Access Key." },
    { value: "s3", label: "Amazon S3", endpoint: "", region: "us-east-1",
      hint: "Leave the endpoint empty; the region is your bucket's (eu-west-2, us-east-1…). Keys: an IAM user allowed to read, write, list and delete in that bucket." },
    { value: "other", label: "Other (S3-compatible)", endpoint: "https://", region: "",
      hint: "Any service with an S3 API (MinIO, a NAS…): its S3 address, region if it needs one, and an access key." },
];

export function offsitePanel(machine) {
    const body = h("div", { class: "panel-body stack" }, h("div", { class: "small muted", text: "Loading…" }));
    const section = h("section", { class: "panel settings-section" },
        h("div", { class: "panel-head" }, icon("upload"), h("h3", { text: "Off-site backups" }), h("span", { class: "sub", text: "A copy of backups away from this PC" })),
        body);
    load();
    return section;

    async function load() {
        let cfg;
        try { cfg = await get(`/machines/${encodeURIComponent(machine)}/offsite`); }
        catch (e) { clear(body).append(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message }))); return; }

        const provider = select(PROVIDERS.map(p => ({ value: p.value, label: p.label })), cfg.provider || "b2");
        const providerHint = h("div", { class: "hint" });
        const endpoint = input({ class: "input mono", value: cfg.endpoint, autocomplete: "off" });
        const region = input({ class: "input mono", value: cfg.region, autocomplete: "off" });
        const bucket = input({ class: "input mono", value: cfg.bucket, autocomplete: "off", placeholder: "my-game-backups" });
        const keyId = input({ class: "input mono", value: cfg.accessKeyId, autocomplete: "off" });
        const secret = input({ type: "password", class: "input mono", autocomplete: "off", placeholder: cfg.hasSecret ? "Saved — paste a new one to replace it" : "The secret key" });
        const prefix = input({ class: "input mono", value: cfg.prefix || "windowsgsm", autocomplete: "off" });
        const keep = input({ type: "number", min: 0, class: "input num", value: cfg.keepCount ?? 7 });
        const enabled = toggle("Off-site backups are on", cfg.enabled, { hint: "Servers with “Also upload each backup off-site” (Backups tab) upload every new backup." });
        const result = h("div");

        function paintProvider() {
            const p = PROVIDERS.find(x => x.value === provider.value) || PROVIDERS[0];
            providerHint.textContent = p.hint;
            endpoint.placeholder = p.endpoint || "(empty for Amazon S3)";
            region.placeholder = p.region || "if the service needs one";
        }
        provider.addEventListener("change", paintProvider);
        paintProvider();

        const values = () => ({
            enabled: enabled.input.checked, provider: provider.value, endpoint: endpoint.value.trim(), region: region.value.trim(), bucket: bucket.value.trim(),
            accessKeyId: keyId.value.trim(), secretAccessKey: secret.value.trim() ? secret.value.trim() : null, prefix: prefix.value.trim(), keepCount: Number(keep.value) || 0,
        });

        const test = h("button", { class: "btn", onclick: () => busy(test, async () => {
            const res = await post(`/machines/${encodeURIComponent(machine)}/offsite/test`, values());
            clear(result).append(h("div", { class: ["callout", res.ok ? "good" : "bad"] }, icon(res.ok ? "checkCircle" : "warn"), h("span", { text: res.message })));
        }, "Couldn't test it") }, icon("checkCircle"), "Test connection");
        const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
            const res = await put(`/machines/${encodeURIComponent(machine)}/offsite`, values());
            secret.value = "";
            secret.placeholder = res.hasSecret ? "Saved — paste a new one to replace it" : "The secret key";
            toast("Off-site settings saved", { type: "good", text: res.enabled ? "Turn on “Also upload each backup off-site” on the servers to send." : "", timeout: 4000 });
        }, "Couldn't save") }, icon("save"), "Save");

        clear(body).append(
            enabled,
            h("div", { class: "form-grid" },
                field("Storage service", provider, { help: "Any S3-compatible storage works. Backblaze B2 and Cloudflare R2 are cheap for backups; R2 has no download fees." }),
                field("Bucket", bucket, { help: "Create a private bucket for your backups first, in the service's own website." }),
                field("Endpoint", endpoint, { help: "The service's S3 address. Leave empty for Amazon S3." }),
                field("Region", region),
                field("Key ID", keyId, { help: "An access key that can read, write, list and delete in that bucket — ideally one made just for this, for just that bucket." }),
                field("Secret key", secret, { help: "Stored encrypted on this PC (Windows data protection), like the other secrets." }),
                field("Folder in the bucket", prefix, { hint: "Each machine and server get their own folder inside.", help: "Backups go to <folder>/<machine id>/server-<id>/ — several WindowsGSM machines can share one bucket." }),
                field("Keep off-site", keep, { hint: "newest backups per server (0 = all)", help: "After each upload, older off-site copies beyond this number are deleted from the bucket. The backups on this PC follow their own keep rules." })),
            providerHint,
            result,
            h("div", { class: "row" }, h("span", { class: "spacer" }), test, save));
    }
}
