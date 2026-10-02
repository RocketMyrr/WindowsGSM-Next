// What the signed-in user may do. The server sends each server's capabilities as a flags string
// ("View, Start, Console" or "All"); the UI hides what you can't do (the API enforces it regardless).
// A server on an offline machine can only be looked at — its last known state, nothing live.

import { store } from "./store.js";

export function can(server, capability) {
    if (!server || !server.can) return false;
    if (capability !== "View" && server.machine && !store.isOnline(server.machine)) return false;
    const caps = String(server.can).split(",").map(s => s.trim());
    return caps.includes("All") || caps.includes(capability);
}

export const isAdmin = () => !!store.me?.canManageUsers;
export const isOwner = () => !!store.me?.isOwner;

/** May install new servers (on one machine, or anywhere): admins, or anyone with Install there (any server's caps carry it). */
export function canInstall(machine = null) {
    if (machine && !store.isOnline(machine)) return false;
    if (isAdmin()) return true;
    for (const s of store.servers.values()) if ((!machine || s.machine === machine) && can(s, "Install")) return true;
    return false;
}

/** Machines the user may install onto, this one first. */
export function installMachines() {
    return [...store.machines.values()].filter(m => m.online !== false && canInstall(m.id));
}
