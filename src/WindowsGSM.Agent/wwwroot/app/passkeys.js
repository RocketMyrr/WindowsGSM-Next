// Passkeys in the browser (WebAuthn): turn the agent's JSON options into what navigator.credentials wants, and
// the device's answer back into JSON. Browsers only offer this on HTTPS (or localhost).

import { post } from "./api.js";

export const passkeysAvailable = () => !!(window.PublicKeyCredential && window.isSecureContext && navigator.credentials);

const fromB64url = s => {
    const b64 = s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4);
    return Uint8Array.from(atob(b64), c => c.charCodeAt(0)).buffer;
};
const toB64url = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

/** Makes a new passkey on this device for the signed-in account. Returns the saved passkey. */
export async function addPasskey(name) {
    const o = await post("/auth/passkeys/options");
    const publicKey = {
        ...o,
        challenge: fromB64url(o.challenge),
        user: { ...o.user, id: fromB64url(o.user.id) },
        excludeCredentials: (o.excludeCredentials || []).map(c => ({ ...c, id: fromB64url(c.id) })),
    };
    const cred = await navigator.credentials.create({ publicKey });
    const response = {
        id: cred.id, rawId: toB64url(cred.rawId), type: cred.type,
        response: {
            attestationObject: toB64url(cred.response.attestationObject),
            clientDataJSON: toB64url(cred.response.clientDataJSON),
            transports: cred.response.getTransports ? cred.response.getTransports() : [],
        },
        clientExtensionResults: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
    };
    return post("/auth/passkeys", { name, response });
}

/** Signs in with a passkey from this device (it shows which ones it has for this address). */
export async function signInWithPasskey() {
    const { token, options: o } = await post("/auth/passkey/options");
    const publicKey = {
        ...o,
        challenge: fromB64url(o.challenge),
        allowCredentials: (o.allowCredentials || []).map(c => ({ ...c, id: fromB64url(c.id) })),
    };
    const cred = await navigator.credentials.get({ publicKey });
    const response = {
        id: cred.id, rawId: toB64url(cred.rawId), type: cred.type,
        response: {
            authenticatorData: toB64url(cred.response.authenticatorData),
            clientDataJSON: toB64url(cred.response.clientDataJSON),
            signature: toB64url(cred.response.signature),
            userHandle: cred.response.userHandle ? toB64url(cred.response.userHandle) : null,
        },
        clientExtensionResults: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
    };
    return post("/auth/passkey/login", { token, response });
}

/** "Chrome on Windows"-ish default name for a new passkey. */
export function deviceName() {
    const ua = navigator.userAgent;
    const os = /iPhone|iPad/.test(ua) ? "iPhone" : /Android/.test(ua) ? "Android phone" : /Mac/.test(ua) ? "Mac" : /Windows/.test(ua) ? "Windows PC" : "This device";
    return os;
}
