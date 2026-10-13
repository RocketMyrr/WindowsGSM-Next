// Agent settings → Off-site backups: one S3-compatible bucket for this machine (Backblaze B2, Cloudflare R2,
// Wasabi, Amazon S3, or anything else that speaks S3) — or a file server over SFTP, FTPS or FTP. Each server then
// chooses "Also upload each backup off-site" on its Backups tab.

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
    // File servers: the server's name or address (with :port if it isn't the usual one), a username and password.
    { value: "sftp", label: "SFTP (SSH file server)", file: true, endpoint: "backup.example.com  or  192.168.1.20:2222",
      hint: "Most NAS boxes, Linux servers and hosting plans offer SFTP. The server's key is trusted the first time and then checked on every connection." },
    { value: "ftps", label: "FTPS (FTP with TLS)", file: true, endpoint: "ftp.example.com  or  192.168.1.20:2121",
      hint: "FTP made private with TLS (\"explicit\", on the usual port 21). A self-signed certificate is trusted the first time and then checked on every connection." },
    { value: "ftps-implicit", label: "FTPS, implicit (port 990)", file: true, endpoint: "ftp.example.com",
      hint: "Older FTPS that is encrypted from the first byte, usually on port 990. Use plain FTPS unless the server says implicit." },
    { value: "ftp", label: "FTP (unencrypted)", file: true, endpoint: "ftp.example.com  or  192.168.1.20", warn: true,
      hint: "Plain FTP sends your password and the backups unencrypted — fine on your own network, risky over the internet. Prefer SFTP or FTPS if the server has them." },
];

export function offsitePanel(machine) {
    const body = h("div", { class: "panel-body stack" }, h("div", { class: "small muted", text: "Loading…" }));
    const section = h("section", { class: "panel settings-section" },
        h("div", { class: "panel-head" }, icon("upload"), h("h2", { text: "Off-site backups" }), h("span", { class: "sub", text: "A copy of backups away from this PC" })),
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
        const pinned = cfg.pinned ? h("div", { class: "tiny faint mono", text: "Server identity trusted: " + cfg.pinned }) : null;

        // Labels follow the kind of storage: a bucket and keys, or a server, username and password.
        const fields = {};
        function paintProvider() {
            const p = PROVIDERS.find(x => x.value === provider.value) || PROVIDERS[0];
            providerHint.textContent = p.hint;
            providerHint.className = p.warn ? "callout warn small" : "hint";
            endpoint.placeholder = p.endpoint || "(empty for Amazon S3)";
            region.placeholder = p.region || "if the service needs one";
            fields.bucket.hidden = fields.region.hidden = !!p.file;
            const label = (f, text) => { const l = f.querySelector("label"); if (l) l.textContent = text; };
            label(fields.endpoint, p.file ? "Server" : "Endpoint");
            label(fields.keyId, p.file ? "Username" : "Key ID");
            label(fields.secret, p.file ? "Password" : "Secret key");
            label(fields.prefix, p.file ? "Folder on the server" : "Folder in the bucket");
            secret.placeholder = cfg.hasSecret ? "Saved — type a new one to replace it" : (p.file ? "The password" : "The secret key");
        }
        provider.addEventListener("change", paintProvider);

        const values = () => ({
            enabled: enabled.input.checked, provider: provider.value, endpoint: endpoint.value.trim(), region: region.value.trim(), bucket: bucket.value.trim(),
            accessKeyId: keyId.value.trim(), secretAccessKey: secret.value ? secret.value : null, prefix: prefix.value.trim(), keepCount: Number(keep.value) || 0,
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

        fields.bucket = field("Bucket", bucket, { help: "Create a private bucket for your backups first, in the service's own website." });
        fields.endpoint = field("Endpoint", endpoint, { help: ["S3: the service's S3 address (empty for Amazon S3).", "A file server: its name or address, with :port when it isn't the usual one (SFTP 22, FTP/FTPS 21, implicit FTPS 990)."] });
        fields.region = field("Region", region);
        fields.keyId = field("Key ID", keyId, { help: "S3: an access key that can read, write, list and delete in that bucket. A file server: an account that can write to the folder." });
        fields.secret = field("Secret key", secret, { help: "Stored encrypted on this PC (Windows data protection), like the other secrets." });
        fields.prefix = field("Folder in the bucket", prefix, { hint: "Each machine and server get their own folder inside.", help: "Backups go to <folder>/<machine id>/server-<id>/ — several WindowsGSM machines can share one place." });
        paintProvider();
        clear(body).append(
            enabled,
            h("div", { class: "form-grid" },
                field("Storage service", provider, { help: "An S3 service (Backblaze B2 and Cloudflare R2 are cheap for backups; R2 has no download fees), or your own file server over SFTP, FTPS or FTP." }),
                fields.bucket, fields.endpoint, fields.region, fields.keyId, fields.secret, fields.prefix,
                field("Keep off-site", keep, { hint: "newest backups per server (0 = all)", help: "After each upload, older off-site copies beyond this number are deleted there. The backups on this PC follow their own keep rules." })),
            providerHint,
            pinned,
            result,
            h("div", { class: "row" }, h("span", { class: "spacer" }), test, save));
    }
}
