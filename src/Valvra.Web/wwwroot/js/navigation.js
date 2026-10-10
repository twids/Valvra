"use strict";
// Routes contain identifiers only. No secret, draft or user-supplied name is a route parameter.
(() => {
    const roots = { vault: "/resources", groups: "/groups", licenses: "/licenses", audit: "/audit", settings: "/settings" };
    const identifier = /^[a-z0-9-]{1,128}$/i;
    function parse(path) {
        const parts = path.replace(/\/$/, "").split("/").slice(1);
        if (path === "/") return { view: "vault" };
        const view = Object.keys(roots).find(key => roots[key] === "/" + parts[0]);
        if (!view) return null;
        if (parts.length === 1) return { view };
        const id = value => identifier.test(value || "") ? value.toLowerCase() : null;
        if (view === "vault" && parts.length === 2 && id(parts[1])) return { view, resourceId: id(parts[1]) };
        if (view === "groups" && id(parts[1])) {
            if (parts.length === 2) return { view, groupId: id(parts[1]) };
            if (parts.length === 4 && parts[2] === "resources" && id(parts[3])) return { view, groupId: id(parts[1]), resourceId: id(parts[3]) };
        }
        return null;
    }
    function url(route) {
        if (!route || !roots[route.view]) throw new Error("Invalid route");
        let path = roots[route.view];
        for (const value of [route.groupId, route.resourceId]) if (value != null && !identifier.test(value)) throw new Error("Invalid route identifier");
        if (route.view === "groups" && route.groupId) path += "/" + route.groupId.toLowerCase();
        if (route.resourceId) {
            if (route.view === "vault") path += "/" + route.resourceId.toLowerCase();
            else if (route.view === "groups" && route.groupId) path += "/resources/" + route.resourceId.toLowerCase();
            else throw new Error("Invalid resource route");
        }
        return path;
    }
    const navigation = Object.freeze({ parse, url, roots: Object.freeze(roots) });
    if (typeof module !== "undefined" && module.exports) module.exports = navigation;
    else window.ValvraNavigation = navigation;
})();
