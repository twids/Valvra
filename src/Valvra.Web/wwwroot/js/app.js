"use strict";
(() => {
    const $ = id => document.getElementById(id);
    const i18n = window.ValvraI18n;
    const navigation = window.ValvraNavigation;
    const t = (key, values) => i18n.message(key, values);
    const state = { session: null, groups: [], resources: [], profiles: [], view: "vault", resource: null, group: null,
        resourceSearch: "", resourceGroup: "", resourceIncludeSubgroups: true, groupSearch: "", groupIncludeSubgroups: true, auditFilters: {} };
    let renderEpoch = 0;
    let routeReady = false, routeUnavailable = false, historyKey, activePath, pendingPath;
    const viewSnapshots = new Map();
    let secretTimer;
    let dialogEpoch = 0;
    let revealedDialog = false;
    let dialogOpener;
    const focusHeading = () => $("page-title").focus();
    const canDisplay = epoch => epoch === dialogEpoch && !document.hidden && document.hasFocus();
    let lastActivity = Date.now();
    const node = (tag, text, className) => { const n = document.createElement(tag); if (text !== undefined) i18n.setText(n, text); if (className) n.className = className; return n; };
    const button = (text, action, style = "") => { const n = node("button", text, "button " + style); n.type = "button"; n.addEventListener("click", () => safely(action)); return n; };
    function routeLink(text, route, style = "") {
        const link = node("a", text, "button " + style); link.href = navigation.url(route); bindRouteLink(link); return link;
    }
    function bindRouteLink(link) {
        link.addEventListener("click", event => {
            if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            if (!routeReady) { pendingPath = link.getAttribute("href"); return; }
            safely(() => navigate(link.getAttribute("href")));
        });
    }
    // Filter snapshots stay in this page's memory. Browser history stores an opaque key only.
    function rememberView() {
        if (!historyKey) return;
        viewSnapshots.set(historyKey, { resourceSearch: state.resourceSearch, resourceGroup: state.resourceGroup,
            resourceIncludeSubgroups: state.resourceIncludeSubgroups, groupSearch: state.groupSearch,
            groupIncludeSubgroups: state.groupIncludeSubgroups, auditFilters: { ...state.auditFilters } });
    }
    function resolveRoute(route) {
        const previousGroup = state.group?.id;
        state.view = route?.view || "vault";
        state.group = route?.groupId ? state.groups.find(group => group.id === route.groupId) || null : null;
        state.resource = route?.resourceId ? state.resources.find(resource => resource.id === route.resourceId) || null : null;
        routeUnavailable = !route || !!route.groupId && !state.group || !!route.resourceId && !state.resource
            || route.view === "audit" && !state.session.isAuditor
            || route.view === "settings" && !state.session.isSystemAdministrator && !state.session.isAccessAdministrator
            || !!state.group && !!state.resource && !groupIds(state.group.id, true).has(state.resource.groupId);
        if (previousGroup !== state.group?.id) { state.groupSearch = ""; state.groupIncludeSubgroups = true; }
    }
    async function navigate(path, { traversal = false, initial = false } = {}) {
        const route = navigation.parse(path);
        rememberView(); closeDialog(); resolveRoute(route);
        const canonical = route ? navigation.url(route) : path;
        if (traversal) {
            historyKey = window.history.state?.valvraEntry;
            const snapshot = viewSnapshots.get(historyKey);
            if (snapshot) Object.assign(state, snapshot, { auditFilters: { ...snapshot.auditFilters } });
        } else if (initial || canonical !== activePath) {
            historyKey = crypto.randomUUID();
            window.history[initial ? "replaceState" : "pushState"]({ valvraEntry: historyKey }, "", canonical);
        }
        activePath = canonical; routeReady = true;
        await render(); if (!initial) focusHeading();
    }
    const has = bit => !!(state.resource && (state.resource.permissions & bit));
    const date = value => value ? new Date(value).toLocaleString(i18n.locale) : "—";
    const rights = value => [[1, "Metadata"], [2, t("Läsa")], [4, t("Ändra")], [8, t("Behörigheter")], [16, "LDAP-test"]].filter(([bit]) => value & bit).map(([, name]) => name).join(", ");
    function notice(message, error = false) { const target = $(error ? "page-error" : "notice"); $(error ? "notice" : "page-error").hidden = true; i18n.setText(target, message); target.hidden = false; }
    async function safely(action) { try { await action(); } catch (error) { const message = error.message || t("Operationen misslyckades."); if ($("dialog").open) $("dialog-error").textContent = message; else notice(message, true); } }
    async function api(path, body) {
        const options = { credentials: "same-origin", cache: "no-store", headers: { "Accept": "application/json", "X-Valvra-Language": i18n.language } };
        if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-CSRF-TOKEN"] = state.session.csrfToken; options.body = JSON.stringify(body); }
        const response = await fetch("/api" + path, options);
        if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.error || (response.status === 401 ? t("Windows-inloggningen kunde inte verifieras.") : t("Operationen kunde inte slutföras."))); }
        return response.status === 204 ? null : response.json();
    }
    function clearSecrets() {
        clearTimeout(secretTimer);
        let cleared = false;
        $("dialog-content").querySelectorAll("input,textarea").forEach(field => { if (field.type === "password" || field.dataset.sensitive) { cleared ||= !!field.value; field.value = ""; } });
        return cleared;
    }
    function updateSecretSubmitState() {
        const missing = Array.from($("dialog-content").querySelectorAll("input,textarea")).some(x => x.required && (x.type === "password" || x.dataset.sensitive) && !x.value.trim());
        $("dialog-actions").querySelectorAll("button").forEach(x => { if (x.type === "submit") x.disabled = missing; });
    }
    function protectFocusLoss() {
        dialogEpoch++; const cleared = clearSecrets();
        if (revealedDialog) { closeDialog(); return; }
        if ($("dialog").open) {
            updateSecretSubmitState();
            if (cleared) $("dialog-error").textContent = t("Hemliga fält har tömts. Övriga uppgifter finns kvar. Ange hemliga värden igen om du vill ersätta dem.");
        }
    }
    function closeDialog() {
        const wasOpen = $("dialog").open;
        dialogEpoch++; revealedDialog = false; clearSecrets(); $("dialog").close(); $("dialog-content").replaceChildren(); $("dialog-actions").replaceChildren();
        if (wasOpen && !document.hidden && document.hasFocus()) (dialogOpener?.isConnected ? dialogOpener : $("page-title")).focus();
    }
    function dialog(title, submitLabel, submit) {
        const opener = $("dialog").open ? dialogOpener : document.activeElement;
        closeDialog(); dialogOpener = opener; $("dialog-title").textContent = title; $("dialog-error").textContent = "";
        const content = $("dialog-content");
        const actions = $("dialog-actions"); actions.append(button(t("Avbryt"), closeDialog));
        if (submit) { const save = node("button", submitLabel, "button primary"); save.type = "submit"; actions.append(save); }
        $("dialog-form").onsubmit = async event => {
            event.preventDefault(); if (!submit || !$("dialog").open || document.hidden || !document.hasFocus()) return;
            const controls = Array.from(actions.querySelectorAll("button")); controls.forEach(x => x.disabled = true);
            try { const message = await submit(); closeDialog(); notice(message === undefined ? t("Ändringen har sparats och auditerats.") : message); }
            catch (error) { $("dialog-error").textContent = error.message; }
            finally { controls.forEach(x => x.disabled = false); updateSecretSubmitState(); }
        };
        $("dialog").showModal(); $("dialog-title").focus(); return content;
    }
    function field(parent, label, type = "text", value = "", required = false) {
        const id = "field-" + crypto.randomUUID(); const l = node("label"); l.append(node("span", label)); if (required) l.append(node("span", t(" (obligatoriskt)"))); l.htmlFor = id;
        const input = type === "textarea" ? node("textarea") : node("input"); input.id = id;
        if (type !== "textarea") input.type = type; input.value = value ?? ""; input.required = required; input.autocomplete = "off";
        if (type === "password" || type === "textarea") input.dataset.sensitive = "true";
        input.addEventListener("input", () => { updateSecretSubmitState(); input.removeAttribute("aria-invalid"); });
        parent.append(l, input); return input;
    }
    function select(parent, label, items, selected) {
        const id = "field-" + crypto.randomUUID(); const l = node("label", label); l.htmlFor = id; const s = node("select"); s.id = id;
        items.forEach(([value, text]) => { const o = node("option", text); o.value = value; s.append(o); });
        if (selected !== undefined && selected !== null) s.value = selected; parent.append(l, s); return s;
    }
    function hint(parent, text) { parent.append(node("p", text, "hint")); }
    function empty(parent, title, text) { const box = node("div", undefined, "empty"); box.append(node("h2", title), node("p", text)); parent.append(box); }
    function heading(title, description, actions = []) { i18n.setText($("page-title"), title); document.title = title + " · Valvra"; i18n.setText($("page-description"), description); $("page-actions").replaceChildren(...actions); }
    function table(parent, headers, rows) {
        const wrap = node("div", undefined, "table-wrap"); const grid = node("table"); const head = node("thead"); const tr = node("tr");
        const title = parent.querySelector("h2,h3")?.textContent || $("dialog-title").textContent || $("page-title").textContent;
        wrap.tabIndex = 0; wrap.setAttribute("role", "region"); wrap.setAttribute("aria-label", title + t(" – tabell, kan rullas i sidled"));
        grid.append(node("caption", title, "visually-hidden"));
        headers.forEach(x => { const th = node("th", x || t("Åtgärder")); th.scope = "col"; tr.append(th); }); head.append(tr); grid.append(head); const body = node("tbody");
        rows.forEach(row => { const r = node("tr"); row.forEach(value => { const cell = node("td"); cell.append(value instanceof Node ? value : document.createTextNode(value ?? "")); r.append(cell); }); body.append(r); });
        body.querySelectorAll("tr").forEach(row => {
            const context = row.children[0].querySelector("strong")?.textContent || row.children[0].textContent;
            row.querySelectorAll("button").forEach(action => action.setAttribute("aria-label", action.textContent + " · " + context));
        });
        grid.append(body); wrap.append(grid); parent.append(wrap); return grid;
    }
    function panel(parent, title, actions = []) { const p = node("section", undefined, "panel"); const top = node("div", undefined, "panel-heading"); top.append(node("h2", title)); const a = node("div", undefined, "actions"); a.append(...actions); top.append(a); p.append(top); parent.append(p); return p; }
    async function load() {
        [state.groups, state.resources, state.profiles] = await Promise.all([api("/groups"), api("/resources"), api("/ldap-profiles")]);
        if (state.resourceGroup && !state.groups.some(x => x.id === state.resourceGroup)) state.resourceGroup = "";
        if (!routeReady) {
            await navigate(window.location.pathname, { initial: true });
            const queued = pendingPath; pendingPath = null;
            if (queued) await navigate(queued);
        }
        else { resolveRoute(navigation.parse(window.location.pathname)); await render(); }
    }
    async function render() {
        const settingsEpoch = ++renderEpoch;
        $("content").replaceChildren(); i18n.setText($("breadcrumb"), { vault: t("Resurser"), groups: t("Resursgrupper"), licenses: t("Licensöversikt"), audit: t("Auditlogg"), settings: t("Inställningar") }[state.view]);
        document.querySelectorAll("[data-view]").forEach(x => { const current = x.dataset.view === state.view; x.classList.toggle("active", current); if (current) x.setAttribute("aria-current", "page"); else x.removeAttribute("aria-current"); });
        if (routeUnavailable) {
            heading(t("Vyn är inte tillgänglig"), t("Länken är ogiltig, innehållet är borttaget eller du saknar behörighet."), [routeLink(t("← Alla resurser"), {view: "vault"})]); return;
        }
        if (state.view === "vault") { if (state.resource) await resourcePage(); else resourcesPage(); }
        else if (state.view === "groups") { if (state.resource) await resourcePage(); else if (state.group) groupPage(); else groupsPage(); }
        else if (state.view === "licenses") await licenseOverview();
        else if (state.view === "settings") await window.ValvraSettings.render({state, api, node, button, field, panel, table, hint, heading, dialog, notice, t, render, refreshSession, isCurrent: () => settingsEpoch === renderEpoch});
        else await auditPage();
    }
    function resourcesPage() {
        heading(t("Dina resurser"), t("Lösenord och licenser samlade kring resurserna de tillhör."), state.groups.some(x => x.canManage) ? [button(t("+ Ny resurs"), createResource, "primary")] : []);
        const content = $("content");
        if (!state.resources.length) { empty(content, t("Inga resurser ännu"), state.session.isAccessAdministrator ? t("Skapa en resursgrupp, lägg till resurser och tilldela åtkomst för att öppna valvet.") : t("Be en resursägare eller behörighetsadministratör tilldela dig åtkomst.")); return; }
        const toolbar = node("div", undefined, "toolbar"); const search = node("input"); search.placeholder = t("Sök bland dina resurser…"); search.setAttribute("aria-label", t("Sök resurser")); search.value = state.resourceSearch; toolbar.append(search);
        const filter = select(toolbar, t("Resursgrupp"), [["", t("Alla grupper")], ...sortedGroups(state.groups).map(x => [x.id, groupPath(x.id).map(g => g.name).join(" / ")])], state.resourceGroup);
        const tree = node("div", undefined, "check-row"); const descendants = field(tree, t("Inkludera undergrupper"), "checkbox"); descendants.checked = state.resourceIncludeSubgroups; descendants.disabled = !filter.value; toolbar.append(tree); content.append(toolbar);
        const count = node("p", undefined, "visually-hidden"); count.setAttribute("role", "status"); content.append(count);
        const cards = node("div", undefined, "cards"); content.append(cards);
        const paint = () => {
            cards.replaceChildren();
            const resources = scopedResources(filter.value, descendants.checked, search.value);
            count.textContent = resources.length + t(" resurser visas.");
            resourceCards(cards, resources);
            if (!resources.length) empty(cards, t("Inga träffar"), t("Prova ett annat sökord eller en annan grupp."));
        }; search.addEventListener("input", () => { state.resourceSearch = search.value; paint(); });
        filter.addEventListener("change", () => { state.resourceGroup = filter.value; descendants.disabled = !filter.value; paint(); });
        descendants.addEventListener("change", () => { state.resourceIncludeSubgroups = descendants.checked; paint(); }); paint();
    }
    function sortedGroups(groups) { return [...groups].sort((a, b) => a.name.localeCompare(b.name, i18n.locale) || a.id.localeCompare(b.id)); }
    function groupPath(id) {
        const byId = new Map(state.groups.map(group => [group.id, group])); const path = [], visited = new Set();
        let group = byId.get(id);
        while (group && !visited.has(group.id) && path.length < 128) { visited.add(group.id); path.unshift(group); group = byId.get(group.parentId); }
        return path;
    }
    function groupIds(id, includeSubgroups) {
        if (!state.groups.some(group => group.id === id)) return new Set();
        const ids = new Set([id]); if (!includeSubgroups) return ids;
        const children = new Map();
        state.groups.forEach(group => { if (!children.has(group.parentId)) children.set(group.parentId, []); children.get(group.parentId).push(group.id); });
        const pending = [id];
        for (let index = 0; index < pending.length; index++) for (const child of children.get(pending[index]) || [])
            if (!ids.has(child)) { ids.add(child); pending.push(child); }
        return ids;
    }
    function scopedResources(id, includeSubgroups, query = "") {
        const ids = id ? groupIds(id, includeSubgroups) : null;
        const term = query.toLocaleLowerCase(i18n.locale);
        return state.resources.filter(resource => (!ids || ids.has(resource.groupId)) && resource.name.toLocaleLowerCase(i18n.locale).includes(term));
    }
    function resourceCards(parent, resources, headingLevel = "h2") {
        resources.forEach(resource => {
            const card = node("article", undefined, "card"); const top = node("div", undefined, "card-top"); top.append(node("div", "◇", "resource-icon"), node("span", resource.canManage ? t("Administrerar") : t("Tilldelad åtkomst"), "badge"));
            const path = groupPath(resource.groupId).map(group => group.name).join(" / ");
            card.append(top, node(headingLevel, resource.name), node("p", path || t("Resursgrupp")));
            const actions = node("div", undefined, "actions"); const open = routeLink(t("Öppna resurs →"), state.view === "groups" && state.group
                ? {view: "groups", groupId: state.group.id, resourceId: resource.id} : {view: "vault", resourceId: resource.id});
            open.setAttribute("aria-label", t("Öppna resurs · ") + resource.name); actions.append(open); card.append(actions); parent.append(card);
        });
    }
    function groupActions(group) {
        if (!group.canManage) return [];
        return [button(t("Behörigheter"), () => accessDialog(0, group)), button(t("Byt namn"), () => renameDialog(0, group)), button(t("Flytta"), () => moveDialog(0, group)), button(t("Ta bort"), () => deleteTarget(0, group), "danger")];
    }
    function groupManagement(group) {
        const details = node("details", undefined, "group-management"), summary = node("summary", t("Hantera grupp"));
        summary.setAttribute("aria-label", t("Hantera grupp · ") + group.name);
        const actions = node("div", undefined, "actions"); actions.append(...groupActions(group));
        actions.querySelectorAll("button").forEach(action => action.setAttribute("aria-label", action.textContent + " · " + group.name));
        details.append(summary, actions); return details;
    }
    function groupTree(parent, groups, nested = true) {
        const list = node("ul", undefined, "group-tree"); list.setAttribute("role", "list"); parent.append(list);
        const available = new Set(groups.map(group => group.id)), visited = new Set();
        const append = (group, container, depth) => {
            if (visited.has(group.id)) return; visited.add(group.id);
            const item = node("li"), row = node("div", undefined, "group-item"), text = node("div", undefined, "group-summary");
            const open = routeLink(group.name, {view: "groups", groupId: group.id}, "group-open"); open.setAttribute("aria-label", t("Öppna grupp · ") + group.name);
            text.append(open, node("p", t("Synliga resurser: {count}", {count: scopedResources(group.id, true).length})));
            row.append(text); if (group.canManage) row.append(groupManagement(group)); item.append(row); container.append(item);
            const children = sortedGroups(groups.filter(child => child.parentId === group.id && !visited.has(child.id)));
            if (nested && children.length && depth < 128) {
                const branch = node("ul", undefined, "group-tree"); branch.setAttribute("role", "list"); item.append(branch);
                children.forEach(child => append(child, branch, depth + 1));
            }
        };
        sortedGroups(groups.filter(group => !nested || !available.has(group.parentId))).forEach(group => append(group, list, 0));
        // Missing parents and malformed cycles never trigger extra metadata requests.
        sortedGroups(groups).forEach(group => { if (!visited.has(group.id)) append(group, list, 0); });
    }
    function groupBreadcrumb(id, resourceName) {
        const navigation = node("nav", undefined, "group-breadcrumb"); navigation.setAttribute("aria-label", t("Gruppsökväg"));
        const root = routeLink(t("Resursgrupper"), {view: "groups"}); navigation.append(root);
        const path = groupPath(id);
        path.forEach((group, index) => {
            const separator = node("span", "/"); separator.setAttribute("aria-hidden", "true"); navigation.append(separator);
            const current = !resourceName && index === path.length - 1;
            const entry = current ? node("span", group.name) : routeLink(group.name, {view: "groups", groupId: group.id});
            if (current) entry.setAttribute("aria-current", "page"); navigation.append(entry);
        });
        if (resourceName) { const separator = node("span", "/"); separator.setAttribute("aria-hidden", "true"); const current = node("span", resourceName); current.setAttribute("aria-current", "page"); navigation.append(separator, current); }
        $("content").append(navigation);
    }
    function groupsPage() {
        const canCreate = state.session.isAccessAdministrator || state.groups.some(x => x.canManage);
        heading(t("Resursgrupper"), t("Organisera resurser i ett träd. Tilldelningar ärvs till undergrupper och resurser."), canCreate ? [button(t("+ Ny grupp"), createGroup, "primary")] : []);
        if (!state.groups.length) { empty($("content"), t("Inga synliga grupper"), t("Grupper visas när du har metadataåtkomst eller rätt att administrera dem.")); return; }
        const p = panel($("content"), t("Gruppstruktur"));
        groupTree(p, state.groups);
    }
    function groupPage() {
        const group = state.group;
        const actions = [routeLink(t("← Alla resursgrupper"), {view: "groups"})];
        if (group.canManage) actions.push(button(t("+ Ny resurs"), createResource, "primary"), button(t("+ Ny undergrupp"), createGroup));
        if (group.canManage) actions.push(groupManagement(group));
        heading(group.name, t("Resurser och undergrupper som du har åtkomst till."), actions); groupBreadcrumb(group.id);
        const children = state.groups.filter(child => child.parentId === group.id);
        if (children.length) groupTree(panel($("content"), t("Undergrupper")), children, false);
        const resources = panel($("content"), t("Resurser")); const body = node("div", undefined, "panel-body"); resources.append(body);
        const toolbar = node("div", undefined, "toolbar"); const search = node("input"); search.setAttribute("aria-label", t("Sök resurser")); search.placeholder = t("Sök bland dina resurser…"); search.value = state.groupSearch; toolbar.append(search);
        const tree = node("div", undefined, "check-row"); const descendants = field(tree, t("Inkludera undergrupper"), "checkbox"); descendants.checked = state.groupIncludeSubgroups; toolbar.append(tree); body.append(toolbar);
        const count = node("p", undefined, "hint"); count.setAttribute("role", "status"); const cards = node("div", undefined, "cards"); body.append(count, cards);
        const paint = () => {
            cards.replaceChildren(); const visible = scopedResources(group.id, descendants.checked, search.value);
            i18n.setText(count, t("Synliga resurser: {count}", {count: visible.length})); resourceCards(cards, visible, "h3");
            if (!visible.length) empty(cards, t("Inga synliga resurser i gruppen"), t("Kontrollera sökningen och valet av undergrupper. Bara resurser du har åtkomst till visas."));
        };
        search.addEventListener("input", () => { state.groupSearch = search.value; paint(); });
        descendants.addEventListener("change", () => { state.groupIncludeSubgroups = descendants.checked; paint(); }); paint();
    }
    async function resourcePage() {
        const resource = state.resource;
        const epoch = renderEpoch;
        const actions = [routeLink(state.view === "groups" ? t("← Till resursgruppen") : t("← Alla resurser"), state.view === "groups" && state.group ? {view: "groups", groupId: state.group.id} : {view: "vault"})];
        if (resource.canManage) actions.push(button(t("Behörigheter"), () => accessDialog(1, resource)), button(t("Byt namn"), () => renameDialog(1, resource)), button(t("Flytta"), () => moveDialog(1, resource)));
        if (has(4)) actions.push(button(t("Papperskorg"), recycleBin));
        if (resource.canManage) actions.push(button(t("Ta bort resurs"), () => deleteTarget(1, resource), "danger"));
        heading(resource.name, t("Din åtkomst: ") + (rights(resource.permissions) || t("Behörighetsadministration")), actions);
        if (state.view === "groups") groupBreadcrumb(resource.groupId, resource.name);
        if (!has(1)) { empty($("content"), t("Du administrerar åtkomsten"), t("Tilldela metadata-, läs- eller ändringsrätt för att använda resursens innehåll.")); return; }
        const [secrets, licenses] = await Promise.all([api(`/resources/${resource.id}/secrets`), api(`/resources/${resource.id}/licenses`)]);
        if (epoch !== renderEpoch) return;
        const passwords = panel($("content"), t("Lösenord"), has(4) ? [button(t("+ Lägg till lösenord"), () => secretDialog(), "primary")] : []);
        if (!secrets.length) empty(passwords, t("Inga lösenord sparade"), t("Lägg till ett konto som hör till den här resursen."));
        else table(passwords, [t("Konto"), "Version", t("Åtgärder")], secrets.map(secret => {
            const actions = node("div", undefined, "actions");
            if (has(2)) actions.append(button(t("Visa"), () => revealSecret(secret)), button(t("Kopiera"), () => copySecret(secret)));
            if (has(4)) actions.append(button(t("Ändra"), () => secretDialog(secret)), button(t("Ta bort"), () => deleteSecret(secret), "danger"));
            actions.append(button(t("Historik"), () => historyDialog(secret)));
            if (has(2) && has(16) && secret.ldapProfileId) actions.append(button("LDAP-test", () => ldapTest(secret)));
            return [secret.title, String(secret.currentVersion), actions];
        }));
        const licensePanel = panel($("content"), t("Licenser"), has(4) ? [button(t("+ Lägg till licens"), () => licenseDialog(), "primary")] : []);
        licenseTable(licensePanel, licenses, true);
    }
    function createGroup() {
        let name, parent;
        const body = dialog(t("Ny resursgrupp"), t("Skapa grupp"), async () => { await api("/groups", { name: name.value, parentId: parent.value || null }); await load(); });
        name = field(body, t("Namn"), "text", "", true); name.maxLength = 200;
        parent = select(body, t("Överordnad grupp"), [...(state.session.isAccessAdministrator ? [["", t("Ingen – skapa rotgrupp")]] : []), ...state.groups.filter(x => x.canManage).map(x => [x.id, x.name])], state.group?.id);
        hint(body, t("Behörigheter och ägare från den överordnade gruppen ärvs."));
    }
    function createResource() {
        let name, group;
        const body = dialog(t("Ny resurs"), t("Skapa resurs"), async () => { const result = await api("/resources", { name: name.value, groupId: group.value }); await load(); await navigate(navigation.url({view: "vault", resourceId: result.id})); });
        name = field(body, t("Resursnamn"), "text", "", true); name.maxLength = 200;
        group = select(body, t("Resursgrupp"), state.groups.filter(x => x.canManage).map(x => [x.id, x.name]), state.group?.id);
    }
    function renameDialog(kind, target) {
        let name; const body = dialog(t("Byt namn"), t("Spara"), async () => { await api(`/${kind === 0 ? "groups" : "resources"}/${target.id}/rename`, { name: name.value, revision: target.revision }); await load(); });
        name = field(body, t("Namn"), "text", target.name, true); name.maxLength = 200;
    }
    function deleteTarget(kind, target) {
        const body = dialog(t("Ta bort · ") + target.name, t("Ta bort"), async () => { await api(`/targets/${kind}/${target.id}/delete`, { deleted: true, revision: target.revision }); await load(); await navigate(navigation.url({view: kind === 0 ? "groups" : "vault"})); });
        body.append(node("p", t("Endast tomma grupper och resurser kan tas bort. Resurser med bevarad historik måste behållas.")));
    }
    async function recycleBin() {
        const epoch = ++dialogEpoch;
        const [secrets, licenses] = await Promise.all([api(`/resources/${state.resource.id}/deleted-secrets`), api(`/resources/${state.resource.id}/deleted-licenses`)]);
        if (!canDisplay(epoch)) return;
        const body = dialog(t("Papperskorg · ") + state.resource.name);
        table(body, [t("Post"), t("Typ"), ""], [...secrets.map(x => [x.title, t("Lösenord"), button(t("Återställ"), async () => { await api(`/secrets/${x.id}/deleted`, { deleted: false, revision: x.revision }); closeDialog(); await render(); })]),
            ...licenses.map(x => [x.product, t("Licens"), button(t("Återställ"), async () => { await api(`/licenses/${x.id}/deleted`, { deleted: false, revision: x.revision }); closeDialog(); await render(); })])]);
        if (!secrets.length && !licenses.length) hint(body, t("Papperskorgen är tom."));
    }
    function secretDialog(secret) {
        safely(async () => {
            const epoch = ++dialogEpoch;
            const payload = secret && has(2) ? await api(`/secrets/${secret.id}/reveal`, {}) : { username: "", password: "", notes: "" };
            if (!canDisplay(epoch)) return;
            let title, username, password, notes, profile;
            const body = dialog(secret ? t("Ändra lösenord") : t("Lägg till lösenord"), t("Spara"), async () => {
                const data = { title: title.value, payload: { username: username.value, password: password.value, notes: notes.value }, ldapProfileId: profile.value || null, revision: secret?.revision || 0 };
                await api(secret ? `/secrets/${secret.id}/update` : `/resources/${state.resource.id}/secrets`, data); await render();
            });
            if (secret && !has(2)) hint(body, t("Du saknar läsrätt. Alla hemliga fält ersätts med de värden du anger här."));
            title = field(body, t("Benämning"), "text", secret?.title || "", true); title.maxLength = 200;
            username = field(body, t("Användarnamn"), "text", payload.username); username.dataset.sensitive = "true"; username.maxLength = 512;
            password = field(body, t("Lösenord"), "password", payload.password, true); password.maxLength = 65536;
            body.append(button(t("Generera lösenord"), () => { const bytes = crypto.getRandomValues(new Uint8Array(24)); password.value = Array.from(bytes, x => x.toString(16).padStart(2, "0")).join(""); updateSecretSubmitState(); }));
            notes = field(body, t("Hemliga anteckningar"), "textarea", payload.notes); notes.maxLength = 65536;
            profile = select(body, t("LDAP-testprofil"), [["", t("Ingen")], ...state.profiles.map(x => [x.id, x.name])], secret?.ldapProfileId || "");
        });
    }
    async function revealSecret(secret, version) {
        const epoch = ++dialogEpoch;
        const payload = await api(`/secrets/${secret.id}/reveal`, { version: version ?? null });
        if (!canDisplay(epoch)) return;
        const body = dialog(secret.title + (version ? ` · version ${version}` : ""));
        revealedDialog = true;
        [ [t("Användarnamn"), payload.username], [t("Lösenord"), payload.password], [t("Anteckningar"), payload.notes] ].forEach(([label, value]) => { const input = field(body, label, "textarea", value); input.readOnly = true; input.classList.add("secret-value"); });
        hint(body, t("Visningen är auditerad. Fälten töms automatiskt efter 30 sekunder eller när sidan tappar fokus."));
        secretTimer = setTimeout(closeDialog, 30000);
    }
    async function copySecret(secret) {
        const epoch = ++dialogEpoch;
        const payload = await api(`/secrets/${secret.id}/reveal`, { copy: true });
        if (!canDisplay(epoch)) return;
        await navigator.clipboard.writeText(payload.password); notice(t("Lösenordet har kopierats. Töm urklipp när du är klar."));
    }
    function deleteSecret(secret) {
        const body = dialog(t("Ta bort lösenord"), t("Ta bort"), async () => { await api(`/secrets/${secret.id}/deleted`, { deleted: true, revision: secret.revision }); await render(); });
        body.append(node("p", t("Ta bort {title}? Historiska versioner bevaras krypterade.", {title: secret.title})));
    }
    async function historyDialog(secret) {
        const epoch = ++dialogEpoch;
        const versions = await api(`/secrets/${secret.id}/versions`); if (!canDisplay(epoch)) return;
        const body = dialog(t("Versionshistorik · ") + secret.title);
        table(body, ["Version", t("Datum"), t("Ändrad av"), t("Åtgärder")], versions.map(version => {
            const actions = node("div", undefined, "actions");
            if (has(2)) actions.append(button(t("Visa"), () => revealSecret(secret, version.version)));
            if (has(2) && has(4) && version.version !== secret.currentVersion) actions.append(button(t("Återställ"), () => {
                const confirmation = dialog(t("Återställ tidigare version"), t("Återställ"), async () => { await api(`/secrets/${secret.id}/restore`, { version: version.version, revision: secret.revision }); await render(); });
                confirmation.append(node("p", t("Version {version} blir en ny aktuell version. Återställningen auditeras.", {version: version.version})));
            }));
            return [String(version.version), date(version.createdAt), version.createdBy, actions];
        }));
    }
    function ldapTest(secret) {
        const body = dialog(t("Testa sparat lösenord mot LDAP"), t("Genomför ett test"), async () => {
            const result = await api(`/secrets/${secret.id}/ldap-test`, {});
            const messages = [t("Autentiseringen lyckades."), t("Autentiseringen nekades. Lösenord, kontostatus eller policy kan vara orsaken."), t("Anslutningen kunde inte verifieras.")];
            return messages[result.result] || result.result;
        });
        body.append(node("p", t("Testet använder det aktuella sparade kontot och lösenordet. Ett misslyckat försök kan bidra till kontolåsning i AD.")));
        hint(body, t("Ett bind-försök utförs utan automatiska återförsök. Kontot kan testas högst en gång per fem minuter i denna tjänsteinstans."));
    }
    function directoryPicker(parent) {
        const kind = select(parent, t("Typ"), [["0", t("Användare")], ["1", t("AD-grupp")]]); const query = field(parent, t("Sök i Active Directory"), "text"); query.maxLength = 128;
        const results = select(parent, t("Välj träff"), [["", t("Sök och välj en träff")]]); let subjects = [];
        const status = node("p", undefined, "hint"); status.setAttribute("role", "status"); parent.append(status);
        parent.append(button(t("Sök"), async () => { subjects = await api(`/directory?query=${encodeURIComponent(query.value)}&kind=${kind.value}`); results.replaceChildren(); subjects.forEach(subject => { const o = node("option", subject.name); o.value = subject.id; results.append(o); }); if (!subjects.length) { const o = node("option", t("Inga träffar")); o.value = ""; results.append(o); } status.textContent = subjects.length + t(" träffar. Välj en träff i listan."); }));
        kind.addEventListener("change", () => { subjects = []; results.replaceChildren(); });
        return () => { const selected = subjects.find(x => x.id === results.value); if (!selected) throw new Error(t("Sök och välj en användare eller grupp först.")); return selected; };
    }
    async function accessDialog(kind, target) {
        const epoch = ++dialogEpoch;
        const access = await api(`/access/${kind}/${target.id}`); if (!canDisplay(epoch)) return;
        const body = dialog(t("Behörigheter · ") + target.name);
        hint(body, t("Tilldelningar summeras. Ärvd åtkomst ändras på den överordnade gruppen. Ägare och behörighetsadministratörer kan tilldela sig själva läsrätt."));
        const actions = node("div", undefined, "actions"); actions.append(button(t("+ Tilldela åtkomst"), () => grantDialog(kind, target)), button(t("+ Lägg till ägare"), () => ownerDialog(kind, target))); body.append(actions);
        table(body, [t("Identitet"), t("Rättigheter"), t("Giltighet"), ""], access.grants.map(grant => {
            const direct = grant.targetKind === kind && grant.targetId === target.id;
            return [grant.subjectId, rights(grant.permissions), grant.expiresAt ? `${date(grant.startsAt)} – ${date(grant.expiresAt)}` : t("Tills vidare"),
                direct ? button(t("Återkalla"), async () => { const epoch = dialogEpoch; await api(`/grants/${grant.id}/revoke`, {}); if (canDisplay(epoch)) await accessDialog(kind, target); }) : node("span", t("Ärvd"), "badge")];
        }));
        body.append(node("h3", t("Resursägare")));
        table(body, [t("Identitet"), ""], access.owners.map(owner => [owner.subjectId,
            owner.targetKind === kind && owner.targetId === target.id ? button(t("Ta bort ägare"), async () => { const epoch = dialogEpoch; await api(`/owners/${owner.id}/remove`, {}); if (canDisplay(epoch)) await accessDialog(kind, target); }) : node("span", t("Ärvd"), "badge")]));
    }
    function grantDialog(kind, target) {
        let pick, temporary, start, expiry; const checks = [];
        const body = dialog(t("Tilldela åtkomst · ") + target.name, t("Tilldela"), async () => {
            const subject = pick(); const temp = temporary.value === "temporary";
            const permissions = temp ? 3 : checks.filter(x => x.input.checked).reduce((value, x) => value | x.bit, 1);
            await api("/grants", { targetKind: kind, targetId: target.id, subjectKind: subject.kind, provider: subject.provider, subjectId: subject.id,
                permissions, startsAt: temp ? new Date(start.value).toISOString() : null, expiresAt: temp ? new Date(expiry.value).toISOString() : null }); await load();
        });
        pick = directoryPicker(body);
        temporary = select(body, t("Giltighet"), [["permanent", t("Tills vidare")], ["temporary", t("Tillfällig läsrätt – endast användare")]]);
        const permissions = node("fieldset"); permissions.append(node("legend", t("Rättigheter"))); body.append(permissions);
        [[2, t("Läsa hemligheter")], [4, t("Ändra innehåll")], [8, t("Hantera behörigheter")], [16, t("Testa mot LDAP")]].forEach(([bit, label]) => { const row = node("div", undefined, "check-row"); const input = field(row, label, "checkbox"); input.value = "on"; checks.push({ bit, input }); permissions.append(row); });
        const times = node("div", undefined, "field-row"); const localDate = date => new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
        start = field(times, t("Från"), "datetime-local", localDate(new Date())); expiry = field(times, t("Till"), "datetime-local", localDate(new Date(Date.now() + 3600000))); body.append(times); times.hidden = true;
        temporary.addEventListener("change", () => { const temp = temporary.value === "temporary"; times.hidden = !temp; start.required = expiry.required = temp; checks.forEach(x => { x.input.disabled = temp; if (temp) x.input.checked = x.bit === 2; }); });
        hint(body, t("Metadataåtkomst ingår. Vid tillfällig rätt är Från och Till obligatoriska. Tillfällig rätt stoppar framtida utlämning men kan inte återkalla ett redan kopierat lösenord."));
    }
    function ownerDialog(kind, target) {
        let pick; const body = dialog(t("Lägg till ägare · ") + target.name, t("Lägg till ägare"), async () => { const subject = pick(); await api("/owners", { targetKind: kind, targetId: target.id, subjectKind: subject.kind, provider: subject.provider, subjectId: subject.id }); await load(); });
        pick = directoryPicker(body); hint(body, t("Ägaren får hantera åtkomst inom resursen eller gruppens underträd."));
    }
    function moveDialog(kind, target) {
        let destination; const body = dialog(t("Flytta · ") + target.name);
        destination = select(body, t("Ny resursgrupp"), state.groups.filter(x => x.canManage && x.id !== target.id).map(x => [x.id, x.name]));
        body.append(button(t("Förhandsgranska åtkomst"), async () => {
            const epoch = ++dialogEpoch;
            const selected = destination.value; const preview = await api(`/move/${kind}/${target.id}/preview`, { destinationGroupId: selected, revision: target.revision });
            if (!canDisplay(epoch)) return;
            const summary = dialog(t("Bekräfta flytt · ") + target.name, t("Flytta"), async () => { await api(`/move/${kind}/${target.id}`, { destinationGroupId: selected, revision: target.revision }); await load(); });
            hint(summary, t("Följande tilldelningar blir tillämpliga efter flytten. För en grupp påverkas även dess underträd; direkta tilldelningar längre ned bevaras."));
            table(summary, [t("Identitet"), t("Rättigheter"), t("Gäller till")], preview.grants.map(x => [x.subjectId, rights(x.permissions), date(x.expiresAt)]));
            table(summary, [t("Ägare")], preview.owners.map(x => [x.subjectId]));
        }, "primary"));
    }
    function licenseTable(parent, licenses, scoped) {
        if (!licenses.length) { empty(parent, t("Inga licenser ännu"), t("Registrera programlicenser, platser och giltighetstid.")); return; }
        table(parent, [t("Produkt"), t("Platser"), t("Giltig till"), t("Åtgärder")], licenses.map(license => {
            const resource = state.resources.find(x => x.id === license.resourceId); const actions = node("div", undefined, "actions");
            if (resource?.permissions & 2) actions.append(button(t("Visa nyckel"), () => revealLicense(license)));
            if (resource?.permissions & 4) actions.append(button(t("Ändra"), () => { state.resource = resource; licenseDialog(license); }));
            if (resource?.permissions & 4) actions.append(button(t("Ta bort"), () => {
                const body = dialog(t("Ta bort licens · ") + license.product, t("Ta bort"), async () => { await api(`/licenses/${license.id}/deleted`, { deleted: true, revision: license.revision }); await render(); });
                body.append(node("p", t("Licensen flyttas till resursens papperskorg. Tilldelningar bevaras för återställning.")));
            }, "danger"));
            actions.append(button(t("Tilldelningar"), () => assignmentDialog(license, resource)));
            actions.append(button(t("Historik"), () => licenseHistory(license, resource)));
            const product = node("div"); product.append(node("strong", license.product), node("p", scoped ? license.vendor : (resource?.name || "") + " · " + license.vendor, "hint"));
            const expiry = node("span", date(license.expiresAt), license.expiresAt && new Date(license.expiresAt) < new Date(Date.now() + 30 * 86400000) ? "badge warn" : "");
            return [product, `${license.assignedSeats} / ${license.seats}`, expiry, actions];
        }));
    }
    async function licenseOverview() {
        const epoch = renderEpoch;
        const writableResources = state.resources.filter(x => (x.permissions & 5) === 5);
        heading(t("Licensöversikt"), t("Tilldelade platser och kommande utgångsdatum för dina synliga resurser."),
            writableResources.length ? [button(t("+ Lägg till licens"), () => licenseDialog(null, writableResources), "primary")] : []);
        const licenses = (await Promise.all(state.resources.filter(x => x.permissions & 1).map(x => api(`/resources/${x.id}/licenses`)))).flat();
        if (epoch !== renderEpoch) return;
        const stats = node("div", undefined, "summary-grid"); const expiring = licenses.filter(x => x.expiresAt && new Date(x.expiresAt) < new Date(Date.now() + 30 * 86400000)).length;
        [[licenses.length, t("Registrerade licenser")], [licenses.reduce((a, x) => a + x.assignedSeats, 0), t("Tilldelade platser")], [expiring, t("Utgångna eller förfaller inom 30 dagar")]].forEach(([value, label]) => { const stat = node("div", undefined, "summary-card"); stat.append(node("strong", String(value)), node("span", label)); stats.append(stat); }); $("content").append(stats);
        licenseTable(panel($("content"), t("Programlicenser")), licenses, false);
    }
    function licenseDialog(license, writableResources) {
            const resourceId = license?.resourceId || state.resource?.id;
            let resourcePicker;
            let product, vendor, reference, seats, expiry, key, notes, change;
            const body = dialog(license ? t("Ändra licens") : t("Lägg till licens"), t("Spara"), async () => {
                const mode = Number(change.value);
                if (mode === 1 && !key.value.trim()) throw new Error(t("Ange en ny licensnyckel eller välj att behålla eller tömma nyckeln."));
                await api(license ? `/licenses/${license.id}/update` : "/licenses", { resourceId: resourcePicker ? resourcePicker.value : resourceId, product: product.value, vendor: vendor.value, purchaseReference: reference.value,
                    seats: Number(seats.value), expiresAt: expiry.value ? new Date(expiry.value + "T23:59:59").toISOString() : null,
                    secretChange: mode, payload: mode === 1 ? { licenseKey: key.value, notes: notes.value } : null, revision: license?.revision || 0 }); await render();
            });
            if (writableResources) resourcePicker = select(body, t("Resurs"), writableResources.map(x => [x.id, x.name]));
            product = field(body, t("Produkt"), "text", license?.product || "", true); vendor = field(body, t("Leverantör"), "text", license?.vendor || "");
            reference = field(body, t("Inköpsreferens"), "text", license?.purchaseReference || ""); seats = field(body, t("Antal platser"), "number", String(license?.seats || 1), true); seats.min = "1"; seats.max = "1000000";
            expiry = field(body, t("Giltig till"), "date", license?.expiresAt?.slice(0, 10) || "");
            change = select(body, t("Licensnyckel och hemliga anteckningar"), license ? [["0", t("Behåll befintliga värden")], ["1", t("Ersätt med nya värden")], ["2", t("Töm värdena uttryckligen")]] : [["1", t("Spara ny licensnyckel")]], license ? "0" : "1");
            key = field(body, t("Ny licensnyckel"), "password"); notes = field(body, t("Nya hemliga anteckningar"), "textarea"); key.maxLength = notes.maxLength = 65536;
            const update = () => { key.value = notes.value = ""; key.disabled = notes.disabled = change.value !== "1"; key.required = change.value === "1"; updateSecretSubmitState(); };
            change.addEventListener("change", update); update();
            hint(body, t("Metadata kan ändras utan att läsa nyckeln. Ersättning och tömning skapar en ny krypterad version; tidigare värden bevaras i historiken."));
            hint(body, t("Vid ersättning är ny licensnyckel obligatorisk. Spara blir tillgänglig när den har fyllts i."));
    }
    async function revealLicense(license, version) {
        const epoch = ++dialogEpoch;
        const payload = await api(`/licenses/${license.id}/reveal`, { version: version ?? null });
        if (!canDisplay(epoch)) return;
        const body = dialog(t("Licensnyckel · ") + license.product + (version ? ` · version ${version}` : ""));
        revealedDialog = true;
        const key = field(body, t("Licensnyckel"), "textarea", payload.licenseKey); key.readOnly = true;
        const notes = field(body, t("Anteckningar"), "textarea", payload.notes); notes.readOnly = true;
        hint(body, t("Visningen är auditerad. Fälten töms efter 30 sekunder eller när sidan tappar fokus.")); secretTimer = setTimeout(closeDialog, 30000);
    }
    async function licenseHistory(license, resource) {
        const epoch = ++dialogEpoch;
        const versions = await api(`/licenses/${license.id}/versions`); if (!canDisplay(epoch)) return;
        const body = dialog(t("Versionshistorik · ") + license.product);
        table(body, ["Version", t("Datum"), t("Ändrad av"), t("Åtgärder")], versions.map(version => {
            const actions = node("div", undefined, "actions");
            if (resource?.permissions & 2) actions.append(button(t("Visa"), () => revealLicense(license, version.version)));
            if ((resource?.permissions & 6) === 6 && version.version !== license.currentVersion) actions.append(button(t("Återställ"), () => {
                const confirmation = dialog(t("Återställ licensversion"), t("Återställ"), async () => { await api(`/licenses/${license.id}/restore`, { version: version.version, revision: license.revision }); await render(); });
                hint(confirmation, t("Version {version} sparas som en ny version. Nuvarande metadata behålls.", {version: version.version}));
            }));
            return [String(version.version), date(version.createdAt), version.createdBy, actions];
        }));
    }
    async function assignmentDialog(license, resource) {
        const epoch = ++dialogEpoch;
        const assignments = await api(`/licenses/${license.id}/assignments`); if (!canDisplay(epoch)) return;
        const body = dialog(t("Tilldelningar · ") + license.product);
        hint(body, t("{assigned} av {seats} platser tilldelade.", {assigned: license.assignedSeats, seats: license.seats}));
        if (resource?.permissions & 4) body.append(button(t("+ Tilldela platser"), () => {
            let mode, pick, target, seats; const form = dialog(t("Tilldela licensplatser"), t("Tilldela"), async () => {
                const subject = mode.value === "user" ? pick() : null;
                await api("/assignments", { licenseId: license.id, resourceId: mode.value === "resource" ? target.value : null,
                    userProvider: subject?.provider || null, userId: subject?.id || null, seats: Number(seats.value) }); await render();
            });
            mode = select(form, t("Tilldela till"), [["user", t("Användare")], ["resource", t("Resurs")]]);
            const users = node("div"); pick = directoryPicker(users); users.querySelector("select").disabled = true; form.append(users);
            const resources = node("div"); target = select(resources, t("Resurs"), state.resources.filter(x => x.permissions & 1).map(x => [x.id, x.name])); form.append(resources); resources.hidden = true;
            mode.addEventListener("change", () => { users.hidden = mode.value !== "user"; resources.hidden = mode.value !== "resource"; });
            seats = field(form, t("Antal platser"), "number", "1", true); seats.min = "1"; seats.max = String(license.seats);
        }));
        table(body, [t("Tilldelad till"), t("Platser"), t("Datum"), ""], assignments.map(x => [x.userId || state.resources.find(r => r.id === x.resourceId)?.name || x.resourceId, String(x.seats), date(x.createdAt),
            resource?.permissions & 4 ? button(t("Återta"), async () => { await api(`/assignments/${x.id}/remove`, {}); await render(); closeDialog(); }) : ""]));
    }
    function auditScopeLabel(view) {
        if (!view.signatureValid) return t("Auditkontext kan inte verifieras.");
        const scope = view.event?.scope;
        if (!scope) return t("Tjänsteövergripande / äldre händelse");
        if (!Array.isArray(scope.groupPath) || !scope.groupPath.length) return t("Auditkontext kan inte verifieras.");
        return [scope.resourceName, scope.groupPath[0]?.name].filter(x => typeof x === "string" && x.length).join(" · ");
    }
    async function auditPage() {
        const epoch = renderEpoch;
        heading(t("Auditlogg"), t("Signerade händelser i separat databas. Tider visas i din lokala tidszon."));
        let targets = { resources: [], groups: [] }, targetsUnavailable = false;
        try { targets = await api("/audit/targets"); }
        catch { targetsUnavailable = true; }
        if (epoch !== renderEpoch) return;
        const toolbar = node("div", undefined, "audit-filters");
        const filter = (label, type) => { const group = node("div"); toolbar.append(group); return field(group, label, type); };
        const from = filter(t("Från"), "datetime-local"), to = filter(t("Till (exklusive)"), "datetime-local");
        const actor = filter(t("Aktörens ID"), "text"), action = filter(t("Operation"), "text"); $("content").append(toolbar);
        const targetFilter = (label, options) => {
            const names = new Map(); options.forEach(x => names.set(x.name, (names.get(x.name) || 0) + 1));
            const group = node("div"); toolbar.append(group);
            return select(group, label, [["", t("Alla")], ...options.map(x => [x.id, names.get(x.name) > 1 ? x.name + " · " + x.id : x.name])]);
        };
        const resource = targetFilter(t("Resurs"), targets.resources), group = targetFilter(t("Resursgrupp"), targets.groups);
        resource.disabled = group.disabled = targetsUnavailable;
        if (targetsUnavailable) hint(toolbar, t("Filterval kunde inte läsas. Auditlistan kan fortfarande visas."));
        if (targets.invalidEventCount > 0) hint(toolbar, t("Korrupta auditrader har upptäckts. Filtervalen använder endast verifierade händelser."));
        const tree = node("div", undefined, "check-row"); toolbar.append(tree);
        const descendants = field(tree, t("Inkludera undergrupper"), "checkbox"); descendants.checked = true;
        hint(toolbar, t("Filtren använder tillhörigheten när händelsen registrerades. Även borttagna resurser och grupper kan väljas."));
        const saved = state.auditFilters;
        for (const [name, input] of Object.entries({from, to, actor, action, resource, group})) input.value = saved[name] || "";
        descendants.checked = saved.descendants !== false;
        const panelNode = panel($("content"), t("Händelser")); const results = node("div"); panelNode.append(results); let offset = saved.offset || 0, paintEpoch = 0;
        const remember = () => { state.auditFilters = {from:from.value, to:to.value, actor:actor.value, action:action.value, resource:resource.value, group:group.value, descendants:descendants.checked, offset}; };
        [from,to,actor,action,resource,group,descendants].forEach(input => { input.addEventListener("input", remember); input.addEventListener("change", remember); });
        const count = node("p", undefined, "hint"); count.setAttribute("role", "status"); panelNode.append(count);
        function query() { const params = new URLSearchParams({ offset }); if (from.value) params.set("from", new Date(from.value).toISOString()); if (to.value) params.set("to", new Date(to.value).toISOString()); if (actor.value) params.set("actorId", actor.value); if (action.value) params.set("action", action.value); if (resource.value) params.set("resourceId", resource.value); if (group.value) { params.set("groupId", group.value); params.set("includeSubgroups", String(descendants.checked)); } return params.toString(); }
        const paint = async () => {
            remember(); const requestEpoch = ++paintEpoch;
            const events = await api("/audit?" + query()); if (epoch !== renderEpoch || requestEpoch !== paintEpoch) return; results.replaceChildren();
            count.textContent = events.length + t(" händelser visas på sida ") + (offset / 200 + 1) + ".";
            if (!events.length) { empty(results, t("Inga händelser"), t("Ändra filtret eller gå till föregående sida.")); return; }
            table(results, [t("Tid"), t("Aktör"), t("Operation"), t("Resurs / resursgrupp"), t("Resultat"), t("Verifiering")], events.map(x => [date(x.event.timestamp), node("span", x.event.actorId, "audit-id"),
                x.event.action, auditScopeLabel(x),
                x.event.outcome, node("span", x.signatureValid ? t("Giltig signatur") : t("Ogiltig signatur"), "badge" + (x.signatureValid ? "" : " error"))]));
        };
        const filterActions = node("div", undefined, "actions filter-actions"); toolbar.append(filterActions);
        filterActions.append(button(t("Filtrera"), async () => { offset = 0; await paint(); }), button(t("Exportera denna sida"), async () => {
            const response = await fetch("/api/audit/export?" + query(), { credentials: "same-origin", cache: "no-store", headers: {"X-Valvra-Language": i18n.language} }); if (!response.ok) throw new Error(t("Auditexporten misslyckades."));
            const url = URL.createObjectURL(await response.blob()); const link = node("a"); link.href = url; link.download = "valvra-audit.json"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        }));
        const paging = node("div", undefined, "actions"); paging.append(button(t("← Föregående"), async () => { offset = Math.max(0, offset - 200); await paint(); }), button(t("Nästa →"), async () => { offset += 200; await paint(); })); $("content").append(paging); await paint();
    }
    document.querySelectorAll("[data-view],.brand").forEach(bindRouteLink);
    window.addEventListener("popstate", () => {
        if (routeReady && window.location.pathname !== activePath) safely(() => navigate(window.location.pathname, { traversal: true }));
    });
    $("close-dialog").addEventListener("click", closeDialog); $("dialog").addEventListener("cancel", event => { event.preventDefault(); closeDialog(); });
    $("dialog").addEventListener("keydown", event => {
        if (event.key !== "Tab") return;
        const controls = Array.from($("dialog").querySelectorAll('button,input,select,textarea,a[href],[tabindex="0"]'))
            .filter(x => !x.disabled && x.getClientRects().length);
        const first = controls[0], last = controls[controls.length - 1];
        if (!first) return;
        if (event.shiftKey && (document.activeElement === first || document.activeElement === $("dialog-title"))) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    });
    $("dialog-form").addEventListener("invalid", event => { event.target.setAttribute("aria-invalid", "true"); i18n.setText($("dialog-error"), t("Kontrollera fältet ”{field}”. {reason}", {field: event.target.labels[0].textContent, reason: i18n.validation(event.target)})); }, true);
    document.addEventListener("valvra:languagechange", () => safely(async () => {
        if (!state.session || $("dialog").open) return;
        await render();
    }));
    $("refresh").addEventListener("click", () => safely(async () => { await load(); notice(t("Vyn har uppdaterats.")); })); window.addEventListener("blur", protectFocusLoss);
    document.addEventListener("visibilitychange", () => { if (document.hidden) protectFocusLoss(); });
    ["pointerdown", "keydown"].forEach(type => document.addEventListener(type, () => lastActivity = Date.now()));
    setInterval(() => { if (Date.now() - lastActivity > 300000) protectFocusLoss(); }, 10000);
    async function refreshSession() {
        state.session = await api("/session"); i18n.setText($("identity"), state.session.displayName);
        $("audit-nav").hidden = !state.session.isAuditor;
        if ($("settings-nav")) $("settings-nav").hidden = !state.session.isAccessAdministrator && !state.session.isSystemAdministrator;
        if (routeReady) resolveRoute(navigation.parse(window.location.pathname));
    }
    safely(async () => { await i18n.ready; await refreshSession(); await load(); });
})();
