// "Report a problem": GitHub's bug-report form for WindowsGSM Next, with this version filled in. It only opens a
// page in the browser — nothing is sent from here; the person writing the report decides what goes in it.

import { store } from "./store.js";

export const REPO = "RocketMyrr/WindowsGSM-Next";

/** The new-issue form (.github/ISSUE_TEMPLATE/bug_report.yml), with the "version" field pre-filled. */
export function reportUrl() {
    const version = String(store.info?.version || store.machine?.version || "").replace(/^v/i, "");
    const q = new URLSearchParams({ template: "bug_report.yml" });
    if (version) q.set("version", version);
    return `https://github.com/${REPO}/issues/new?${q}`;
}

export const KNOWN_ISSUES_URL = `https://github.com/${REPO}/blob/main/docs/KNOWN-ISSUES.md`;

export function openReport() { window.open(reportUrl(), "_blank", "noopener"); }
