import { h, icon } from "../dom.js";
import { setCrumbs } from "../shell.js";
import { empty } from "../ui.js";

export default function notFound(host) {
    setCrumbs({ label: "Not found" });
    host.append(empty("search", "There's nothing here", "The page you asked for doesn't exist — it may have moved, or the server was deleted.",
        h("a", { class: "btn primary", href: "/" }, icon("grid"), "Back to overview")));
}
