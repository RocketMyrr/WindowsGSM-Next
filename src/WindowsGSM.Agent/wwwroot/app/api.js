// REST client for /api/v2. Sends the CSRF header on every call, parses JSON, and turns every non-2xx
// into an ApiError carrying the server's human message and stable code.

export class ApiError extends Error {
    constructor(status, code, message, details) {
        super(message);
        this.status = status;
        this.code = code;
        this.details = details || [];
    }
}

const listeners = new Set();

/** Called with no arguments when any request comes back 401 (signed out / session revoked). */
export function onUnauthorized(fn) { listeners.add(fn); return () => listeners.delete(fn); }

export async function api(path, { method = "GET", body, raw = false, signal, headers = {} } = {}) {
    const init = { method, headers: { "X-WGSM-CSRF": "1", ...headers }, credentials: "same-origin", signal };
    if (body instanceof FormData) init.body = body;
    else if (body !== undefined) { init.body = JSON.stringify(body); init.headers["Content-Type"] = "application/json"; }

    let res;
    try { res = await fetch("/api/v2" + path, init); }
    catch (e) {
        if (e.name === "AbortError") throw e;
        throw new ApiError(0, "network", "Can't reach the agent. Check that it's running and your connection is up.");
    }

    if (res.status === 401 && !path.startsWith("/auth/login") && !path.startsWith("/setup")) {
        for (const fn of listeners) fn();
    }
    if (raw) return res;
    if (res.status === 204 || res.status === 202 && res.headers.get("content-length") === "0") return null;

    const text = await res.text();
    let data = null;
    if (text) { try { data = JSON.parse(text); } catch { data = text; } }
    if (!res.ok) {
        const message = data && data.error ? data.error : res.status === 429 ? "Too many attempts — wait a minute and try again." : `Request failed (${res.status}).`;
        throw new ApiError(res.status, data && data.code || "http_" + res.status, message, data && data.details);
    }
    return data;
}

export const get = (path, opts) => api(path, { ...opts, method: "GET" });
export const post = (path, body, opts) => api(path, { ...opts, method: "POST", body: body ?? {} });
export const put = (path, body, opts) => api(path, { ...opts, method: "PUT", body });
export const patch = (path, body, opts) => api(path, { ...opts, method: "PATCH", body });
export const del = (path, opts) => api(path, { ...opts, method: "DELETE" });

/** Path under a server: srv("m-abc", "7", "/console") → /machines/m-abc/servers/7/console ("local" = this machine) */
export const srv = (machine, id, rest = "") => `/machines/${encodeURIComponent(machine)}/servers/${encodeURIComponent(id)}${rest}`;

/**
 * Upload with progress (fetch can't report upload progress). Resolves with the parsed JSON body.
 */
export function upload(path, formData, onProgress) {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open("POST", "/api/v2" + path);
        xhr.setRequestHeader("X-WGSM-CSRF", "1");
        xhr.upload.onprogress = e => { if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total); };
        xhr.onload = () => {
            let data = null;
            try { data = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
            if (xhr.status >= 200 && xhr.status < 300) resolve(data);
            else reject(new ApiError(xhr.status, data && data.code || "upload", data && data.error || `Upload failed (${xhr.status}).`, data && data.details));
        };
        xhr.onerror = () => reject(new ApiError(0, "network", "Upload failed — the connection dropped."));
        xhr.send(formData);
    });
}
