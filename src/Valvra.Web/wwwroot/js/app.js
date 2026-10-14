"use strict";
(() => {
    const $ = id => document.getElementById(id);
    const i18n = window.ValvraI18n;
    const navigation = window.ValvraNavigation;
    const t = (key, values) => i18n.message(key, values);
    const state = { session: null, groups: [], resources: [], profiles: [], view: "vault", resource: null, group: null,
        resourceSearch: "", resourceGroup: "", resourceIncludeSubgroups: true, groupSearch: "", groupIncludeSubgroups: true, resourceLayout:"cards", resourceSort:"name", collapsedGroups:new Set(), contentFilters:{}, licenseFilters:{}, auditFilters: {} };
    let renderEpoch = 0;
    let routeReady = false, routeUnavailable = false, historyKey, activePath, pendingPath;
    const viewSnapshots = new Map();
    let secretTimer;
    let countdownTimer;
    let pendingMetadataRead;
    let dialogEpoch = 0;
    let dialogInstance=0,activeSubmission=null,resetReplacement;
    let revealedDialog = false;
    let dialogOpener;
    let dialogBack;
    let noticeTimer;
    let closeActionMenu;
    let actionMenuElement;
    const focusHeading = () => $("page-title").focus();
    const canDisplay = epoch => epoch === dialogEpoch && !document.hidden && document.hasFocus();
    let lastActivity = Date.now();
    const node = (tag, text, className) => { const n = document.createElement(tag); if (text !== undefined) i18n.setText(n, text); if (className) n.className = className; return n; };
    const button = (text, action, style = "", guard = true) => { const n = node("button", text, "button " + style); n.type = "button"; let running = false; n.addEventListener("click", () => safely(async () => { if (!guard) return action(); if (running) return; running = true; n.setAttribute("aria-disabled", "true"); try { await action(); } finally { running = false; n.removeAttribute("aria-disabled"); } })); return n; };
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
            resourceIncludeSubgroups: state.resourceIncludeSubgroups, resourceLayout:state.resourceLayout, resourceSort:state.resourceSort, groupSearch: state.groupSearch,
            groupIncludeSubgroups: state.groupIncludeSubgroups, licenseFilters:{...state.licenseFilters}, auditFilters: { ...state.auditFilters } });
    }
    function resolveRoute(route) {
        const previousGroup = state.group?.id;
        state.view = route?.view || "vault";
        state.settingsSection = route?.section || (state.session.isAccessAdministrator ? "people" : "directory");
        state.group = route?.groupId ? state.groups.find(group => group.id === route.groupId) || null : null;
        state.resource = route?.resourceId ? state.resources.find(resource => resource.id === route.resourceId) || null : null;
        routeUnavailable = !route || !!route.groupId && !state.group || !!route.resourceId && !state.resource
            || route.view === "audit" && !state.session.isAuditor
            || route.view === "settings" && !state.session.isSystemAdministrator && !state.session.isAccessAdministrator
            || route.view === "settings" && state.settingsSection === "directory" && !state.session.isSystemAdministrator
            || route.view === "settings" && state.settingsSection === "import" && !state.session.isAccessAdministrator
            || route.view === "settings" && state.settingsSection === "people" && !state.session.isAccessAdministrator
            || !!state.group && !!state.resource && !groupIds(state.group.id, true).has(state.resource.groupId);
        if (previousGroup !== state.group?.id) { state.groupSearch = ""; state.groupIncludeSubgroups = true; }
    }
    async function navigate(path, { traversal = false, initial = false } = {}) {
        closeActionMenu?.();
        clearNotice();
        const route = navigation.parse(path);
        rememberView(); closeDialog(); resolveRoute(route);
        const canonical = route ? navigation.url(route) : path;
        if (traversal) {
            historyKey = window.history.state?.valvraEntry;
            const snapshot = viewSnapshots.get(historyKey);
            if (snapshot) Object.assign(state, snapshot, { licenseFilters:{...snapshot.licenseFilters}, auditFilters: { ...snapshot.auditFilters } });
        } else if (initial || canonical !== activePath) {
            historyKey = crypto.randomUUID();
            window.history[initial ? "replaceState" : "pushState"]({ valvraEntry: historyKey }, "", canonical);
        }
        activePath = canonical; routeReady = true;
        await render(); if (!initial) focusHeading();
    }
    const has = bit => !!(state.resource && (state.resource.permissions & bit));
    const date = value => value ? new Date(value).toLocaleString(i18n.locale) : "—";
    const calendarDate=value=>{const valueDate=new Date(value);return valueDate.getFullYear()+"-"+String(valueDate.getMonth()+1).padStart(2,"0")+"-"+String(valueDate.getDate()).padStart(2,"0");};
    const rights = value => [[1, "Metadata"], [2, t("Läsa")], [4, t("Ändra")], [8, t("Behörigheter")], [16, "LDAP-test"]].filter(([bit]) => value & bit).map(([, name]) => name).join(", ");
    function clearNotice() { clearTimeout(noticeTimer); $("notice").hidden = true; }
    function notice(message, error = false) {
        clearNotice();
        const target = $(error ? "page-error" : "notice"); $(error ? "notice" : "page-error").hidden = true;
        const text = node("span", message);
        const dismiss = button(t("Stäng meddelande"), () => { target.hidden = true; focusHeading(); }, "quiet");
        target.replaceChildren(text, dismiss); target.hidden = false;
        if (!error) {
            const expire = () => { if (target.contains(document.activeElement) || target.matches(":hover")) noticeTimer = setTimeout(expire, 1000); else target.hidden = true; };
            noticeTimer = setTimeout(expire, 10000);
        }
    }
    async function safely(action) { try { await action(); } catch (error) { const message = error.message || t("Operationen misslyckades."); if ($("dialog").open) $("dialog-error").textContent = message; else notice(message, true); } }
    async function api(path, body) {
        const options = { credentials: "same-origin", cache: "no-store", headers: { "Accept": "application/json", "X-Valvra-Language": i18n.language } };
        if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-CSRF-TOKEN"] = state.session.csrfToken; options.body = JSON.stringify(body); }
        const response = await fetch("/api" + path, options);
        if (!response.ok) { const error = await response.json().catch(() => ({})); const failure = new Error(error.error || (response.status === 401 ? t("Windows-inloggningen kunde inte verifieras.") : t("Operationen kunde inte slutföras."))); failure.status = response.status; failure.retryAfter = Math.min(120, Math.max(1, Number(response.headers.get("Retry-After")) || 60)); throw failure; }
        if(response.status===204)return null;
        const value=await response.json();
        if(path.startsWith("/audit?")&&Array.isArray(value)){value.snapshotId=response.headers?.get?.("X-Valvra-Audit-Snapshot");value.capturedAt=response.headers?.get?.("X-Valvra-Audit-Captured");value.expiresAt=response.headers?.get?.("X-Valvra-Audit-Expires");}
        return value;
    }
    function clearSecrets() {
        clearTimeout(secretTimer);
        clearInterval(countdownTimer);
        let cleared = false;
        $("dialog-content").querySelectorAll("input,textarea").forEach(field => { if (field.type === "password" || field.dataset.sensitive) { cleared ||= !!field.value; field.value = ""; } });
        return cleared;
    }
    function updateSecretSubmitState() {
        const missing = activeSubmission===dialogInstance || $("dialog-content").dataset.importCleared === "true" || Array.from($("dialog-content").querySelectorAll("input,textarea")).some(x => x.required && (x.type === "password" || x.dataset.sensitive) && !x.value.trim());
        $("dialog-actions").querySelectorAll("button").forEach(x => { if (x.type === "submit") x.disabled = missing; });
    }
    function protectFocusLoss() {
        window.ValvraImport?.clear("blur");
        pendingMetadataRead?.();pendingMetadataRead=null;
        dialogEpoch++; const cleared = clearSecrets();
        resetReplacement?.();
        if (revealedDialog) { closeDialog(); return; }
        if ($("dialog").open) {
            updateSecretSubmitState();
            if (cleared) $("dialog-error").textContent = t("Hemliga fält har tömts. Övriga uppgifter finns kvar. Ange hemliga värden igen om du vill ersätta dem.");
        }
    }
    function closeDialog() {
        dialogBack = null;
        resetReplacement=null;dialogInstance++;
        pendingMetadataRead=null;
        window.ValvraImport?.clear();
        const wasOpen = $("dialog").open;
        dialogEpoch++; revealedDialog = false; clearSecrets(); $("dialog").close(); $("dialog-content").replaceChildren(); $("dialog-content").removeAttribute("aria-busy"); $("dialog-actions").replaceChildren();
        if (wasOpen && !document.hidden && document.hasFocus()) (dialogOpener?.isConnected ? dialogOpener : $("page-title")).focus();
    }
    function dismissDialog() { const back = dialogBack; closeDialog(); if (back && !document.hidden && document.hasFocus()) safely(back); }
    function dialog(title, submitLabel, submit, options = {}) {
        const opener = $("dialog").open ? dialogOpener : document.activeElement;
        closeDialog(); dialogOpener = opener; $("dialog-title").textContent = title; $("dialog-error").textContent = "";
        dialogBack = options.back;
        const content = $("dialog-content");
        content.className = "";
        delete content.dataset.importCleared;
        const actions = $("dialog-actions"); actions.append(button(t(options.back ? "← Tillbaka" : submit ? "Avbryt" : "Stäng"), dismissDialog));
        if (submit) { const save = node("button", submitLabel, "button primary"); save.type = "submit"; actions.append(save); }
        $("dialog-form").onsubmit = async event => {
            event.preventDefault(); if (!submit || !$("dialog").open || document.hidden || !document.hasFocus() || activeSubmission===dialogInstance) return;
            const controls = Array.from(actions.querySelectorAll("button")); controls.forEach(x => x.disabled = true);
            const submittedInstance=dialogInstance;activeSubmission=submittedInstance;let completed=false;
            try { const message = await submit(); if (message === false) return;completed=true;
                const sameDialog=submittedInstance===dialogInstance;if(sameDialog)closeDialog();
                const confirmation = message === undefined ? t("Klart och auditerat: {action}.", {action: String(title)}) : message;
                if (sameDialog && options.afterSave && !document.hidden && document.hasFocus()) await options.afterSave(confirmation); else notice(confirmation);
            }
            catch (error) { if (submittedInstance === dialogInstance) $("dialog-error").textContent = error.message; else notice(error.message, true); }
            finally { if(activeSubmission===submittedInstance)activeSubmission=null;if(!completed&&submittedInstance===dialogInstance){controls.forEach(x => x.disabled = false);updateSecretSubmitState();} }
        };
        $("dialog").showModal(); $("dialog-title").focus(); return content;
    }
    async function metadataDialog(title,read,retry){
        const body=dialog(title),progress=node("p",t("Läser in…"));progress.setAttribute("role","status");body.append(progress);body.setAttribute("aria-busy","true");
        const epoch=dialogEpoch;
        const failed=message=>{progress.remove();body.removeAttribute("aria-busy");body.append(node("p",message),button(t("Försök igen"),retry));};
        const interrupted=()=>failed(t("Läsningen avbröts när sidan tappade fokus. Försök igen för att läsa aktuell metadata."));pendingMetadataRead=interrupted;
        try{const value=await read();if(!canDisplay(epoch))return null;progress.remove();body.removeAttribute("aria-busy");return{body,value};}
        catch(error){if(canDisplay(epoch))failed(error.message);return null;}
        finally{if(pendingMetadataRead===interrupted)pendingMetadataRead=null;}
    }
    function revealCountdown(body){
        const remaining=node("p",undefined,"hint");remaining.setAttribute("role","timer");remaining.setAttribute("aria-live","off");body.append(remaining);
        const started=Date.now();
        const update=()=>{const seconds=Math.max(0,Math.min(30,Math.ceil((30000-(Date.now()-started))/1000)));remaining.textContent=t("Fälten töms om {seconds} sekunder.",{seconds});if(seconds===0)closeDialog();};
        update();countdownTimer=setInterval(update,1000);secretTimer=setTimeout(closeDialog,30000);
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
    function controlGroup(parent){const group=node("div",undefined,"filter-control");parent.append(group);return group;}
    function empty(parent, title, text) { const box = node("div", undefined, "empty"); box.append(node("h2", title), node("p", text)); parent.append(box); }
    function heading(title, description, actions = []) { i18n.setText($("page-title"), title); document.title = title + " · Valvra"; i18n.setText($("page-description"), description); $("page-actions").replaceChildren(...actions); }
    function table(parent, headers, rows) {
        const wrap = node("div", undefined, "table-wrap"); const grid = node("table"); const head = node("thead"); const tr = node("tr");
        const title = parent.querySelector("h2,h3")?.textContent || parent.closest("section")?.querySelector("h2,h3")?.textContent
            || (parent.closest("dialog") ? $("dialog-title").textContent : $("page-title").textContent);
        wrap.tabIndex = 0; wrap.setAttribute("role", "region"); wrap.setAttribute("aria-label", title + t(" – tabell, kan rullas i sidled"));
        grid.append(node("caption", title, "visually-hidden"));
        headers.forEach(x => { const th = node("th", x || t("Åtgärder")); th.scope = "col"; tr.append(th); }); head.append(tr); grid.append(head); const body = node("tbody");
        rows.forEach(row => { const r = node("tr"); row.forEach((value,index) => { const cell = node("td"); cell.dataset.label=String(headers[index] || t("Åtgärder")); cell.append(value instanceof Node ? value : document.createTextNode(value ?? "")); r.append(cell); }); body.append(r); });
        body.querySelectorAll("tr").forEach(row => {
            const context = row.children[0].querySelector("strong")?.textContent || row.children[0].textContent;
            row.querySelectorAll("button").forEach(action => { if(!action.hasAttribute("aria-label"))action.setAttribute("aria-label", action.textContent + " · " + context); });
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
        closeActionMenu?.();
        const settingsEpoch = ++renderEpoch;
        $("content").removeAttribute("aria-busy");
        $("content").replaceChildren(); i18n.setText($("breadcrumb"), { vault: t("Resurser"), groups: t("Resursgrupper"), licenses: t("Licensöversikt"), audit: t("Auditlogg"), settings: t("Administration") }[state.view]);
        document.querySelectorAll("[data-view]").forEach(x => { const current = x.dataset.view === state.view; x.classList.toggle("active", current); if (current) x.setAttribute("aria-current", "page"); else x.removeAttribute("aria-current"); });
        if (routeUnavailable) {
            heading(t("Vyn är inte tillgänglig"), t("Länken är ogiltig, innehållet är borttaget eller du saknar behörighet."), [routeLink(t("← Alla resurser"), {view: "vault"})]);
            if(state.session.isGlobalAdministrator){hint($("content"),t("Kontrollera uttryckliga åtkomstundantag i Administration. Att öppna sidan återställer inte åtkomsten."));$("content").append(routeLink(t("Uttryckliga åtkomstundantag"),{view:"settings",section:"people"}));}return;
        }
        const progress=node("p",t("Läser in…"),"hint");progress.setAttribute("role","status");$("content").append(progress);$("content").setAttribute("aria-busy","true");
        try {
        if (state.view === "vault") { if (state.resource) await resourcePage(); else resourcesPage(); }
        else if (state.view === "groups") { if (state.resource) await resourcePage(); else if (state.group) groupPage(); else groupsPage(); }
        else if (state.view === "licenses") await licenseOverview();
        else if (state.view === "settings") await window.ValvraSettings.render({state, api, node, button, field, select, directoryPicker, panel, table, hint, heading, dialog, notice, t, render, refreshSession, routeLink,
            openImport:() => window.ValvraImport.open({state, api, node, button, field, select, hint, table, dialog, notice, load, t}), isCurrent: () => settingsEpoch === renderEpoch});
        else await auditPage();
        } catch(error) {
            if(settingsEpoch!==renderEpoch)return;
            const failure=node("section",undefined,"panel");failure.append(node("h2",t("Vyn kunde inte läsas")),node("p",error.message));
            failure.append(button(t("Försök igen"),()=>render(),"primary"));$("content").append(failure);
        } finally {
            progress.remove();if(settingsEpoch===renderEpoch)$("content").removeAttribute("aria-busy");
        }
    }
    function appendImportAction() {
        if (!state.session.isAccessAdministrator) return;
        if (state.resources.some(x => (x.permissions & 5) === 5) || state.groups.some(x => x.canManage && (x.permissions & 5) === 5))
            $("page-actions").append(button(t("Importera lösenord"), () => window.ValvraImport.open({state, api, node, button, field, select, hint, table, dialog, notice, load, t})));
    }
    function resourcesPage() {
        heading(t("Dina resurser"), t("Lösenord och licenser samlade kring resurserna de tillhör."), state.groups.some(x => x.canManage) ? [button(t("+ Ny resurs"), createResource, "primary")] : []);
        appendImportAction();
        const content = $("content");
        if (!state.resources.length) { empty(content, t("Inga resurser ännu"), state.session.isAccessAdministrator ? t("Skapa en resursgrupp, lägg till resurser och tilldela åtkomst för att öppna valvet.") : t("Be en resursägare eller behörighetsadministratör tilldela dig åtkomst.")); return; }
        const toolbar = node("div", undefined, "toolbar"); const search = node("input"); search.placeholder = t("Sök bland dina resurser…"); search.setAttribute("aria-label", t("Sök resurser")); search.value = state.resourceSearch; toolbar.append(search);
        const filter = select(controlGroup(toolbar), t("Resursgrupp"), [["", t("Alla grupper")], ...sortedGroups(state.groups).map(x => [x.id, groupPath(x.id).map(g => g.name).join(" / ")])], state.resourceGroup);
        const tree = node("div", undefined, "check-row"); const descendants = field(tree, t("Inkludera undergrupper"), "checkbox"); descendants.checked = state.resourceIncludeSubgroups; descendants.disabled = !filter.value; toolbar.append(tree); content.append(toolbar);
        const count = node("p", undefined, "hint"); count.setAttribute("role", "status"); content.append(count);
        const cards = node("div", undefined, "cards"); content.append(cards);
        const paint = () => {
            cards.replaceChildren();
            const resources = scopedResources(filter.value, descendants.checked, search.value);
            count.textContent = resources.length + t(" resurser visas.");
            resourceCollection(cards, resources);
            if (!resources.length) empty(cards, t("Inga träffar"), t("Prova ett annat sökord eller en annan grupp."));
        }; search.addEventListener("input", () => { state.resourceSearch = search.value; paint(); });
        filter.addEventListener("change", () => { state.resourceGroup = filter.value; descendants.disabled = !filter.value; paint(); });
        descendants.addEventListener("change", () => { state.resourceIncludeSubgroups = descendants.checked; paint(); });
        browseControls(toolbar,paint);
        toolbar.append(button(t("Rensa filter"),()=>{search.value="";filter.value="";descendants.checked=true;descendants.disabled=true;state.resourceSearch="";state.resourceGroup="";state.resourceIncludeSubgroups=true;paint();search.focus();})); paint();
    }
    function browseControls(toolbar, paint) {
        const layout=select(controlGroup(toolbar),t("Visning"),[["cards",t("Kort")],["list",t("Kompakt lista")]],state.resourceLayout);
        const sort=select(controlGroup(toolbar),t("Sortering"),[["name",t("Namn A–Ö")],["reverse",t("Namn Ö–A")],["group",t("Resursgrupp")]],state.resourceSort);
        layout.addEventListener("change",()=>{state.resourceLayout=layout.value;paint();});sort.addEventListener("change",()=>{state.resourceSort=sort.value;paint();});
    }
    function resourceCollection(parent, resources, level="h2") {
        const path=id=>groupPath(id).map(x=>x.name).join(" / ");
        const ordered=[...resources].sort((a,b)=>(state.resourceSort==="group" ? path(a.groupId).localeCompare(path(b.groupId),i18n.locale) : 0)
            || (state.resourceSort==="reverse" ? -1 : 1)*a.name.localeCompare(b.name,i18n.locale)||a.id.localeCompare(b.id));
        parent.className=state.resourceLayout==="list" ? "resource-list" : "cards";
        if(state.resourceLayout!=="list"){resourceCards(parent,ordered,level);return;}
        if(!ordered.length)return;
        table(parent,[t("Namn"),t("Resursgrupp"),t("Din åtkomst"),t("Åtgärder")],ordered.map(resource=>{
            const open=routeLink(t("Öppna resurs →"),state.view==="groups"&&state.group ? {view:"groups",groupId:state.group.id,resourceId:resource.id} : {view:"vault",resourceId:resource.id});
            open.setAttribute("aria-label",t("Öppna resurs · ")+resource.name);
            return [node("strong",resource.name),path(resource.groupId),rights(resource.permissions)||t("Behörighetsadministration"),open];
        })).classList.add("mobile-records");
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
        return [button(t("Behörigheter"), () => accessDialog(0, group)), button(t("Byt namn"), () => renameDialog(0, group)), button(t("Flytta till annan grupp…"), () => moveDialog(0, group)), button(t("Ta bort"), () => deleteTarget(0, group), "danger")];
    }
    function actionMenu(label, actions, context) {
        const wrapper = node("div",undefined,"action-menu"), trigger = node("button",label,"button"); trigger.type = "button";
        if (context) trigger.setAttribute("aria-label", label + " · " + context);
        const menu = node("div",undefined,"action-menu-popup"); menu.id = "menu-" + crypto.randomUUID();
        menu.setAttribute("popover","auto"); menu.setAttribute("role","menu"); menu.setAttribute("aria-label",label + (context ? " · " + context : ""));
        trigger.setAttribute("aria-haspopup","menu"); trigger.setAttribute("aria-controls",menu.id); trigger.setAttribute("aria-expanded","false");
        let open = false;
        const close = () => { if (menu.hidePopover) { if(open)menu.hidePopover(); } else menu.hidden=true; open=false; trigger.setAttribute("aria-expanded","false"); if(actionMenuElement===wrapper)actionMenuElement=null; };
        if (!menu.showPopover) menu.hidden=true;
        actions.forEach(action => {
            action.setAttribute("role","menuitem"); action.tabIndex=-1;
            if (context) action.setAttribute("aria-label",action.textContent + " · " + context);
            action.addEventListener("click",() => { close(); trigger.focus(); },true); menu.append(action);
        });
        const show = () => {
            closeActionMenu?.(); closeActionMenu=close; actionMenuElement=wrapper;
            menu.hidden=false;
            if (menu.showPopover) menu.showPopover(); else menu.hidden=false;
            open=true; trigger.setAttribute("aria-expanded","true");
            const rect=trigger.getBoundingClientRect(), size=menu.getBoundingClientRect();
            menu.style.left=Math.max(8,Math.min(rect.right-size.width,window.innerWidth-size.width-8))+"px";
            menu.style.top=Math.max(8,rect.bottom+size.height+8<=window.innerHeight ? rect.bottom+4 : rect.top-size.height-4)+"px";
            actions[0]?.focus({preventScroll:true});
        };
        trigger.addEventListener("click",()=>{if(open)close();else show();});
        trigger.addEventListener("keydown",event=>{if(event.key==="ArrowDown"){event.preventDefault();show();}});
        menu.addEventListener("toggle",event=>{if(event.newState==="closed"){open=false;trigger.setAttribute("aria-expanded","false");}});
        menu.addEventListener("keydown",event=>{
            const index=actions.indexOf(document.activeElement);
            if (["ArrowDown","ArrowUp","Home","End"].includes(event.key)) { event.preventDefault(); actions[event.key==="Home" ? 0 : event.key==="End" ? actions.length-1 : (index+(event.key==="ArrowDown" ? 1 : -1)+actions.length)%actions.length]?.focus(); }
            else if(event.key==="Escape"){event.preventDefault();close();trigger.focus();}
            else if(event.key==="Tab"){close();trigger.focus({preventScroll:true});}
        });
        wrapper.append(trigger,menu); return wrapper;
    }
    function groupManagement(group) { const menu=actionMenu(t("Hantera grupp"),groupActions(group),group.name);menu.classList.add("group-management");return menu; }
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
                const branch = node("ul", undefined, "group-tree group-tree-branch"); branch.id="branch-"+crypto.randomUUID();branch.dataset.groupId=group.id;branch.hidden=state.collapsedGroups.has(group.id);branch.setAttribute("role", "list"); item.append(branch);
                const toggle=button(t("Undergrupper")+" · "+group.name,()=>{if(state.collapsedGroups.has(group.id))state.collapsedGroups.delete(group.id);else state.collapsedGroups.add(group.id);branch.hidden=state.collapsedGroups.has(group.id);toggle.setAttribute("aria-expanded",String(!branch.hidden));},"quiet");
                toggle.setAttribute("aria-controls",branch.id);toggle.setAttribute("aria-expanded",String(!branch.hidden));row.insertBefore(toggle,row.firstChild);
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
        appendImportAction();
        if (!state.groups.length) { empty($("content"), t("Inga synliga grupper"), t("Grupper visas när du har metadataåtkomst eller rätt att administrera dem.")); return; }
        const setBranches=collapsed=>{state.groups.forEach(x=>{if(collapsed)state.collapsedGroups.add(x.id);else state.collapsedGroups.delete(x.id);});
            document.querySelectorAll(".group-tree-branch").forEach(branch=>{branch.hidden=collapsed;document.querySelector(`[aria-controls="${branch.id}"]`)?.setAttribute("aria-expanded",String(!collapsed));});};
        const p = panel($("content"), t("Gruppstruktur"),[button(t("Visa alla undergrupper"),()=>setBranches(false)),button(t("Dölj alla undergrupper"),()=>setBranches(true))]);
        groupTree(p, state.groups);
    }
    function groupPage() {
        const group = state.group;
        const actions = [routeLink(t("← Alla resursgrupper"), {view: "groups"})];
        if (group.canManage) actions.push(button(t("+ Ny resurs"), createResource, "primary"), button(t("+ Ny undergrupp"), createGroup));
        if (group.canManage) actions.push(groupManagement(group));
        heading(group.name, t("Resurser och undergrupper som du har åtkomst till."), actions); groupBreadcrumb(group.id);
        appendImportAction();
        const children = state.groups.filter(child => child.parentId === group.id);
        if (children.length) groupTree(panel($("content"), t("Undergrupper")), children, false);
        const resources = panel($("content"), t("Resurser")); const body = node("div", undefined, "panel-body"); resources.append(body);
        const toolbar = node("div", undefined, "toolbar"); const search = node("input"); search.setAttribute("aria-label", t("Sök resurser")); search.placeholder = t("Sök bland dina resurser…"); search.value = state.groupSearch; toolbar.append(search);
        const tree = node("div", undefined, "check-row"); const descendants = field(tree, t("Inkludera undergrupper"), "checkbox"); descendants.checked = state.groupIncludeSubgroups; toolbar.append(tree); body.append(toolbar);
        const count = node("p", undefined, "hint"); count.setAttribute("role", "status"); const cards = node("div", undefined, "cards"); body.append(count, cards);
        const paint = () => {
            cards.replaceChildren(); const visible = scopedResources(group.id, descendants.checked, search.value);
            i18n.setText(count, t("Synliga resurser: {count}", {count: visible.length})); resourceCollection(cards, visible, "h3");
            if (!visible.length) empty(cards, t("Inga synliga resurser i gruppen"), t("Kontrollera sökningen och valet av undergrupper. Bara resurser du har åtkomst till visas."));
        };
        search.addEventListener("input", () => { state.groupSearch = search.value; paint(); });
        descendants.addEventListener("change", () => { state.groupIncludeSubgroups = descendants.checked; paint(); });
        browseControls(toolbar,paint);toolbar.append(button(t("Rensa filter"),()=>{search.value="";descendants.checked=true;state.groupSearch="";state.groupIncludeSubgroups=true;paint();search.focus();}));paint();
    }
    async function resourcePage() {
        state.licenseResources=null;
        const resource = state.resource;
        const epoch = renderEpoch;
        if(state.session.isLicenseAdministrator){state.licenseResources=await api("/licenses/destinations");if(epoch!==renderEpoch)return;}
        const actions = [routeLink(state.view === "groups" ? t("← Till resursgruppen") : t("← Alla resurser"), state.view === "groups" && state.group ? {view: "groups", groupId: state.group.id} : {view: "vault"})];
        const secondary=[];
        if (resource.canManage) { actions.push(button(t("Behörigheter"), () => accessDialog(1, resource))); secondary.push(button(t("Byt namn"), () => renameDialog(1, resource)), button(t("Flytta till annan grupp…"), () => moveDialog(1, resource))); }
        if (has(4)) secondary.push(button(t("Papperskorg"), recycleBin));
        if (resource.canManage) secondary.push(button(t("Ta bort resurs"), () => deleteTarget(1, resource), "danger"));
        if(secondary.length)actions.push(actionMenu(t("Resursåtgärder"),secondary));
        const capabilities=[];
        if(has(1))capabilities.push(t("Du kan se listan."));
        if(has(2))capabilities.push(t("Du kan visa och kopiera hemligheter."));
        if(has(4))capabilities.push(t("Du kan lägga till och ändra innehåll."));
        if(resource.canManage)capabilities.push(t("Du kan hantera åtkomst."));
        heading(resource.name,capabilities.join(" "), actions);
        appendImportAction();
        groupBreadcrumb(resource.groupId, resource.name);
        const canAdd=has(1)&&has(4),helpId="resource-content-access";
        const addAction=(label,action)=>{const add=button(t(label),action,"primary");if(!canAdd&&!(label==="+ Lägg till licens"&&state.session.isLicenseAdministrator)){add.disabled=true;add.setAttribute("aria-describedby",helpId);}return add;};
        if(!canAdd){const explanation=node("p",t("För att lägga till lösenord eller licenser krävs både Metadata och Ändra på resursen. Administratörsrollen ger inte innehållsåtkomst automatiskt."),"hint");explanation.id=helpId;$("content").append(explanation);}
        if (!has(1)) { empty($("content"), t("Du administrerar åtkomsten"), t("Öppna Behörigheter för att tilldela innehållsåtkomst på resursen eller dess överordnade grupp."));panel($("content"),t("Lösenord"),[addAction("+ Lägg till lösenord",()=>secretDialog())]);panel($("content"),t("Licenser"),[addAction("+ Lägg till licens",()=>licenseDialog())]);return; }
        const [secretRead,licenseRead]=await Promise.allSettled([api(`/resources/${resource.id}/secrets`),api(`/resources/${resource.id}/licenses`)]);
        if (epoch !== renderEpoch) return;
        const secrets=secretRead.status==="fulfilled"?secretRead.value:[],licenses=licenseRead.status==="fulfilled"?licenseRead.value:[];
        const controls=node("div",undefined,"toolbar"),search=field(controls,t("Sök i resursen"),"search",state.contentFilters[resource.id]||"");
        search.maxLength=200;hint(controls,t("Sök i kontotitlar och produktnamn. Hemliga värden söks aldrig."));
        const count=node("p",undefined,"hint");count.setAttribute("role","status");controls.append(count);
        const records=node("div");$("content").append(controls,records);
        const paint=()=>{
        const query=search.value.trim().toLocaleLowerCase(i18n.locale),visibleSecrets=secrets.filter(x=>x.title.toLocaleLowerCase(i18n.locale).includes(query)),visibleLicenses=licenses.filter(x=>x.product.toLocaleLowerCase(i18n.locale).includes(query));
        state.contentFilters[resource.id]=search.value;records.replaceChildren();count.textContent=t("{shown} av {total} poster visas.",{shown:visibleSecrets.length+visibleLicenses.length,total:secrets.length+licenses.length});
        const passwords = panel(records, t("Lösenord"), [addAction("+ Lägg till lösenord",()=>secretDialog())]);
        if(secretRead.status==="rejected"){hint(passwords,secretRead.reason.message);passwords.append(button(t("Försök igen"),()=>render()));}
        else if (!secrets.length) empty(passwords, t("Inga lösenord sparade"), t(has(4)?"Lägg till ett konto som hör till den här resursen.":"Det finns inga konton här ännu. En person med ändringsrätt kan lägga till dem."));
        else if(!visibleSecrets.length)hint(passwords,t("Inga kontotitlar matchar sökningen."));
        else table(passwords, [t("Konto"), "Version", t("Åtgärder")], visibleSecrets.map(secret => {
            const actions = node("div", undefined, "actions");
            if (has(2)) actions.append(button(t("Visa"), () => revealSecret(secret)), button(t("Kopiera"), () => copySecret(secret)));
            const secondary=[];
            if (has(4)) secondary.push(button(t("Ändra"), () => secretDialog(secret)));
            secondary.push(button(t("Historik"), () => historyDialog(secret)));
            if (has(2) && has(16) && secret.ldapProfileId) secondary.push(button("LDAP-test", () => ldapTest(secret)));
            if (has(4)) secondary.push(button(t("Ta bort"), () => deleteSecret(secret), "danger"));
            actions.append(actionMenu(t("Fler åtgärder"),secondary,secret.title));
            return [secret.title, String(secret.currentVersion), actions];
        })).classList.add("mobile-records");
        const licensePanel = panel(records, t("Licenser"), [addAction("+ Lägg till licens",()=>licenseDialog())]);
        if(licenseRead.status==="rejected"){hint(licensePanel,licenseRead.reason.message);licensePanel.append(button(t("Försök igen"),()=>render()));}
        else if(licenses.length&&!visibleLicenses.length)hint(licensePanel,t("Inga produktnamn matchar sökningen."));else licenseTable(licensePanel, visibleLicenses, true);
        if(secretRead.status==="rejected"||licenseRead.status==="rejected")count.textContent=t("Översikten är ofullständig. Ett innehållsavsnitt kunde inte läsas.");
        };
        controls.append(button(t("Rensa filter"),()=>{search.value="";paint();search.focus();}));search.addEventListener("input",paint);paint();
    }
    function createGroup() {
        let name, parent;
        const body = dialog(t("Ny resursgrupp"), t("Skapa grupp"), async () => { await api("/groups", { name: name.value, parentId: parent.value || null }); await load(); });
        name = field(body, t("Namn"), "text", "", true); name.maxLength = 200;
        parent = select(body, t("Överordnad grupp"), [...(state.session.isAccessAdministrator ? [["", t("Ingen – skapa rotgrupp")]] : []), ...sortedGroups(state.groups.filter(x => x.canManage)).map(x => [x.id, groupPath(x.id).map(g=>g.name).join(" / ") + " · " + x.id])], state.group?.id);
        hint(body, t("Behörigheter och ägare från den överordnade gruppen ärvs."));
    }
    function createResource() {
        let name, group;
        const body = dialog(t("Ny resurs"), t("Skapa resurs"), async () => { const result = await api("/resources", { name: name.value, groupId: group.value }); await load(); await navigate(navigation.url({view: "vault", resourceId: result.id})); });
        name = field(body, t("Resursnamn"), "text", "", true); name.maxLength = 200;
        group = select(body, t("Resursgrupp"), sortedGroups(state.groups.filter(x => x.canManage)).map(x => [x.id, groupPath(x.id).map(g=>g.name).join(" / ") + " · " + x.id]), state.group?.id);
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
            let title, username, password, notes, profile,change,replacementEpoch=0;
            const body = dialog(secret ? t("Ändra lösenord") : t("Lägg till lösenord"), t("Spara"), async () => {
                const replace=change.value==="1";
                if(replace&&!password.value.trim())throw new Error(t("Ange ett lösenord för ersättningen."));
                const data = { title: title.value, secretChange:replace?1:0,payload: replace?{ username: username.value, password: password.value, notes: notes.value }:null, ldapProfileId: profile.value || null, revision: secret?.revision || 0 };
                await api(secret ? `/secrets/${secret.id}/update` : `/resources/${state.resource.id}/secrets`, data); await render();
            });
            if (secret && !has(2)) hint(body, t("Du saknar läsrätt. Metadata kan ändras med bevarade hemligheter. Om du väljer ersättning måste du ange alla hemliga värden som ska sparas."));
            title = field(body, t("Benämning"), "text", secret?.title || "", true); title.maxLength = 200;
            profile = select(body, t("LDAP-testprofil"), [["", t("Ingen")], ...state.profiles.map(x => [x.id, x.name])], secret?.ldapProfileId || "");
            change=select(body,t("Lösenord och hemliga fält"),secret?[["0",t("Behåll befintliga värden")],["1",t("Ersätt alla hemliga fält")]]:[["1",t("Ersätt alla hemliga fält")]],secret?"0":"1");
            const replacement=node("div");body.append(replacement);
            username = field(replacement, t("Användarnamn"), "text"); username.dataset.sensitive = "true"; username.maxLength = 512;
            password = field(replacement, t("Lösenord"), "password","",true); password.maxLength = 65536;
            const generate=button(t("Generera lösenord"), () => { const bytes = crypto.getRandomValues(new Uint8Array(24)); password.value = Array.from(bytes, x => x.toString(16).padStart(2, "0")).join("");replacementEpoch++; updateSecretSubmitState(); });replacement.append(generate);
            notes = field(replacement, t("Hemliga anteckningar"), "textarea"); notes.maxLength = 65536;
            let readSaved;
            const update=()=>{replacement.hidden=change.value!=="1";replacementEpoch++;username.value=password.value=notes.value="";username.disabled=password.disabled=notes.disabled=generate.disabled=change.value!=="1";if(readSaved)readSaved.disabled=change.value!=="1";password.required=change.value==="1";const marker=password.labels?.[0]?.querySelectorAll("span")[1];if(marker)i18n.setText(marker,password.required?t(" (obligatoriskt)"):"");updateSecretSubmitState();};
            change.addEventListener("change",update);update();if(secret)resetReplacement=()=>{change.value="0";update();};
            [username,password,notes].forEach(input=>input.addEventListener("input",()=>replacementEpoch++));
            if(secret&&has(2)){readSaved=button(t("Läs sparade värden för ersättning"),async()=>{
                if(change.value!=="1")throw new Error(t("Välj ersättning först."));const epoch=++dialogEpoch,request=replacementEpoch;
                const payload=await api(`/secrets/${secret.id}/reveal`,{});if(!canDisplay(epoch)||request!==replacementEpoch||change.value!=="1")return;
                username.value=payload.username;password.value=payload.password;notes.value=payload.notes;updateSecretSubmitState();
            });readSaved.disabled=change.value!=="1";replacement.append(readSaved);}
            hint(body,t("Metadata kan sparas utan att läsa lösenordet. Ersättning ändrar användarnamn, lösenord och anteckningar tillsammans; tomma frivilliga fält töms. Efter fokusförlust väljs bevarande igen."));
    }
    async function revealSecret(secret, version) {
        const epoch = ++dialogEpoch;
        const payload = await api(`/secrets/${secret.id}/reveal`, { version: version ?? null });
        if (!canDisplay(epoch)) return;
        const body = dialog(secret.title + (version ? ` · version ${version}` : ""),undefined,undefined,version?{back:()=>historyDialog(secret)}:{});
        revealedDialog = true;
        [ [t("Användarnamn"), payload.username], [t("Lösenord"), payload.password], [t("Anteckningar"), payload.notes] ].forEach(([label, value]) => { const input = field(body, label, "textarea", value); input.readOnly = true; input.classList.add("secret-value"); });
        hint(body, t("Visningen är auditerad. Fälten töms automatiskt efter 30 sekunder eller när sidan tappar fokus."));
        revealCountdown(body);
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
        const loaded=await metadataDialog(t("Versionshistorik · ")+secret.title,()=>api(`/secrets/${secret.id}/versions`),()=>historyDialog(secret));if(!loaded)return;
        const {body,value:versions}=loaded;
        table(body, ["Version", t("Datum"), t("Ändrad av"), t("Åtgärder")], versions.map(version => {
            const actions = node("div", undefined, "actions");
            if (has(2)) actions.append(button(t("Visa"), () => revealSecret(secret, version.version)));
            if (has(2) && has(4) && version.version !== secret.currentVersion) actions.append(button(t("Återställ"), () => {
                const confirmation = dialog(t("Återställ tidigare version"), t("Återställ"), async () => { await api(`/secrets/${secret.id}/restore`, { version: version.version, revision: secret.revision }); await render(); },{back:()=>historyDialog(secret)});
                confirmation.append(node("p", t("Version {version} blir en ny aktuell version. Återställningen auditeras.", {version: version.version})));
            }));
            return [String(version.version), date(version.createdAt), versionCreator(version), actions];
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
    function directoryPicker(parent,options={}) {
        const picker = node("fieldset"); picker.append(node("legend", t("Mottagare"))); parent.append(picker); parent = picker;
        const kind = select(parent, t("Typ"), [["0", t("Användare")], ["1", t("AD-grupp")]]); const query = field(parent, t("Sök i Active Directory"), "text"); query.minLength = 2; query.maxLength = 128;
        const results = select(parent, t("Välj träff"), [["", t("Sök och välj en träff")]]); let subjects = [];
        const status = node("p", undefined, "hint"); status.setAttribute("role", "status"); parent.append(status);
        let searchEpoch = 0;
        const reset = () => {
            searchEpoch++; subjects = []; results.replaceChildren(node("option", t("Sök och välj en träff"))); results.options[0].value = "";
            status.textContent = ""; search.disabled = false; results.removeAttribute("aria-busy");options.changed?.();
        };
        const runSearch = async () => {
            reset();
            const text = query.value.trim();
            if (text.length < 2 || text.length > 128) { status.textContent = t("Ange 2–128 tecken för katalogsökning."); query.focus(); return; }
            const epoch = searchEpoch;
            search.disabled = true; results.setAttribute("aria-busy", "true");
            try {
                const found = await api((options.searchPath||"/directory")+`?query=${encodeURIComponent(text)}&kind=${kind.value}`);
                if (epoch !== searchEpoch || !results.isConnected || !$("dialog").open) return;
                subjects = found;
                subjects.forEach(subject => { const o = node("option", subject.name + (subject.samAccountName?" · "+subject.samAccountName:"")+" · " + subject.id); o.value = subject.id; results.append(o); });
                status.textContent = subjects.length ? subjects.length + t(" träffar. Välj en träff i listan.") : t("Inga träffar");
                results.focus();
            } catch (error) { if (epoch === searchEpoch && results.isConnected) status.textContent = error.message; }
            finally { if (epoch === searchEpoch) { search.disabled = false; results.removeAttribute("aria-busy"); } }
        };
        const search = button(t("Sök"), runSearch, "", false); parent.append(search);
        query.addEventListener("input", reset); kind.addEventListener("change", reset);
        query.addEventListener("keydown", event => { if (event.key === "Enter" && !event.isComposing) { event.preventDefault(); event.stopPropagation(); if (!search.disabled) safely(runSearch); } });
        results.addEventListener("change", () => { const subject = subjects.find(x => x.id === results.value); status.textContent = subject ? subject.name + " · " + subject.id : "";options.changed?.(); });
        const pick = () => { const selected = subjects.find(x => x.id === results.value); if (!selected) throw new Error(t("Sök och välj en användare eller grupp först.")); return selected; };
        pick.onlyUsers = enabled => { if (enabled && kind.value !== "0") { kind.value = "0"; reset(); } kind.disabled = enabled; };
        if(options.usersOnly){pick.onlyUsers(true);kind.hidden=true;kind.previousElementSibling.hidden=true;}
        pick.clear=reset;
        pick.selected=()=>subjects.find(x=>x.id===results.value);
        return pick;
    }
    function identityLabel(access, row) {
        const subject = access.subjects?.find(x => x.provider === row.provider && x.id === row.subjectId && x.kind === row.subjectKind);
        const label = node("div");
        label.append(node("strong", subject?(subject.name+(subject.samAccountName?" · "+subject.samAccountName:"")):t("Identiteten kunde inte slås upp")), node("p", (row.subjectKind === 0 ? t("Användare") : t("AD-grupp")) + " · " + row.provider, "hint"));
        const details = node("details"); details.open = !subject; details.append(node("summary", t("Identitet")), node("p", row.subjectId, "audit-id")); label.append(details); return label;
    }
    function accessSource(row, kind, target) {
        if (row.targetKind === kind && row.targetId === target.id) return node("span", t("Direkt"), "badge");
        const group = state.groups.find(x => x.id === row.targetId);
        const label = t("Ärvd från") + " " + (group ? groupPath(group.id).map(x => x.name).join(" / ") : row.targetId);
        return group ? routeLink(label, {view:"groups", groupId:group.id}) : node("span", label, "badge");
    }
    async function accessDialog(kind, target, feedback) {
        const epoch = ++dialogEpoch;
        const access = await api(`/access/${kind}/${target.id}`); if (!canDisplay(epoch)) return;
        const body = dialog(t("Behörigheter · ") + target.name);
        body.classList.add("permissions-overview");
        if (feedback) { const result = node("p", feedback, "notice"); result.setAttribute("role", "status"); body.append(result); }
        hint(body, t("Tilldelningar summeras. Ärvd åtkomst ändras på den överordnade gruppen. Ägare och behörighetsadministratörer kan tilldela sig själva läsrätt."));
        hint(body, t("Din effektiva innehållsåtkomst just nu: {rights}.", {rights:rights(access.effectivePermissions || 0) || t("Ingen") }));
        hint(body,t("Inloggad identitet: {name} · {provider} · {id}.",{name:state.session.displayName,provider:state.session.provider,id:state.session.subjectId}));
        const actions = node("div", undefined, "actions"); actions.append(button(t("+ Tilldela åtkomst"), () => grantDialog(kind, target)), button(t("+ Lägg till ägare"), () => ownerDialog(kind, target))); body.append(actions);
        body.append(node("h3", t("Tilldelad åtkomst")));
        if (!access.grants.length) hint(body, t("Inga direkta eller ärvda tilldelningar. Ägare och globala administratörer kan fortfarande hantera åtkomst."));
        else table(body, [t("Identitet"), t("Rättigheter"), t("Giltighet"), t("Källa"), ""], access.grants.map(grant => {
            const direct = grant.targetKind === kind && grant.targetId === target.id;
            const controls=node("div",undefined,"actions");if(direct)controls.append(button(t("Ändra åtkomst"),()=>grantDialog(kind,target,grant,access)),button(t("Återkalla"),()=>confirmAccessRemoval("grants",grant,access,kind,target)));
            else controls.append(node("span",t("Ändras i källgruppen"),"hint"));
            return [identityLabel(access, grant), rights(grant.permissions), grant.expiresAt ? `${date(grant.startsAt)} – ${date(grant.expiresAt)}` : t("Tills vidare"), accessSource(grant, kind, target),controls];
        }));
        body.append(node("h3", t("Resursägare")));
        if (!access.owners.length) hint(body, t("Inga resursägare är tilldelade."));
        else table(body, [t("Identitet"), t("Källa"), ""], access.owners.map(owner => [identityLabel(access, owner), accessSource(owner, kind, target),
            owner.targetKind === kind && owner.targetId === target.id ? button(t("Ta bort ägare"), () => confirmAccessRemoval("owners", owner, access, kind, target)) : node("span", t("Ändras i källgruppen"), "hint")]));
    }
    function confirmAccessRemoval(type, row, access, kind, target) {
        const back = () => accessDialog(kind, target);
        const body = dialog(t(type === "grants" ? "Återkalla åtkomst" : "Ta bort ägare") + " · " + target.name, t(type === "grants" ? "Återkalla" : "Ta bort ägare"), async () => {
            await api(`/${type}/${row.id}/${type === "grants" ? "revoke" : "remove"}`, {}); await load();
        }, {back, afterSave:message => accessDialog(kind, target, message)});
        body.append(identityLabel(access, row));
        hint(body, t("Endast denna direkta tilldelning tas bort. Andra tilldelningar och gruppmedlemskap kan fortfarande ge åtkomst."));
        if (kind === 0) hint(body, t("Ändringen gäller även resurser och undergrupper som ärver denna tilldelning."));
    }
    function grantDialog(kind, target, existing, access) {
        let pick, temporary, start, expiry,metadata; const checks = [];
        const body = dialog(t(existing?"Ändra åtkomst · ":"Tilldela åtkomst · ") + target.name, t(existing?"Spara":"Tilldela"), async () => {
            const subject = pick(); const temp = temporary.value === "temporary";
            const permissions = temp ? 3 : checks.filter(x => x.input.checked).reduce((value, x) => value | x.bit,existing?(metadata.checked?1:0):1);
            const startsAt=temporary.value!=="permanent"?(existing?.startsAt&&start.value===initialStart?existing.startsAt:new Date(start.value).toISOString()):null;
            const expiresAt=temp?(existing&&expiry.value===initialExpiry?existing.expiresAt:new Date(expiry.value).toISOString()):null;
            if(temp&&(!start.value||!expiry.value||new Date(expiresAt)<=new Date(startsAt)))throw new Error(t("Till måste vara efter Från."));
            await api(existing?`/grants/${existing.id}/update`:"/grants",existing?{permissions,startsAt,expiresAt,revision:existing.revision}:{ targetKind: kind, targetId: target.id, subjectKind: subject.kind, provider: subject.provider, subjectId: subject.id,
                permissions, startsAt, expiresAt }); await load();
        }, {back:() => accessDialog(kind, target), afterSave:message => accessDialog(kind, target, message)});
        if(existing){body.append(identityLabel(access,existing));const resolved=access.subjects?.find(x=>x.id===existing.subjectId&&x.provider===existing.provider&&x.kind===existing.subjectKind);const subject={id:existing.subjectId,provider:existing.provider,kind:existing.subjectKind,name:resolved?.name||existing.subjectId,samAccountName:resolved?.samAccountName};pick=()=>subject;pick.selected=pick;pick.onlyUsers=()=>{};}
        else pick = directoryPicker(body,{changed:()=>summary()});
        temporary = select(body, t("Giltighet"), [["permanent", t("Tills vidare")],...(existing?.startsAt&&!existing.expiresAt?[["scheduled",t("Från ett startdatum, utan slutdatum")]]:[]),...(!existing||existing.subjectKind===0?[["temporary", t("Tillfällig läsrätt – endast användare")]]:[])],existing?.expiresAt?"temporary":existing?.startsAt?"scheduled":"permanent");
        const preset=select(body,t("Åtkomstuppgift"),[["1",t("Endast se listan")],["3",t("Läsa och kopiera")],["7",t("Läsa och ändra")],["custom",t("Anpassade rättigheter")]],existing?"custom":"1");
        const permissions = node("fieldset"); permissions.append(node("legend", t("Rättigheter"))); body.append(permissions);
        if(existing){metadata=field(permissions,t("Visa innehållslistor (Metadata)"),"checkbox");metadata.checked=!!(existing.permissions&1);}
        else hint(permissions, t("Metadataåtkomst ingår alltid. Välj inga ytterligare rättigheter för endast metadata."));
        [[2, t("Läsa hemligheter")], [4, t("Ändra innehåll")], [8, t("Hantera behörigheter")], [16, t("Testa mot LDAP")]].forEach(([bit, label]) => { const row = node("div", undefined, "check-row"); const input = field(row, label, "checkbox"); input.value = "on";input.checked=!!(existing?.permissions&bit); checks.push({ bit, input }); permissions.append(row); });
        const times = node("div", undefined, "field-row"); const localDate = date => new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 19);
        const initialStart=localDate(existing?.startsAt?new Date(existing.startsAt):new Date()),initialExpiry=localDate(existing?.expiresAt?new Date(existing.expiresAt):new Date(Date.now()+3600000));
        start = field(controlGroup(times), t("Från"), "datetime-local", initialStart); expiry = field(controlGroup(times), t("Till"), "datetime-local", initialExpiry); body.append(times); times.hidden = !existing?.expiresAt;
        const review=node("section",undefined,"panel");review.setAttribute("aria-label",t("Kontrollera tilldelningen"));review.append(node("h3",t("Kontrollera tilldelningen")));const reviewText=node("p");reviewText.setAttribute("role","status");review.append(reviewText);body.append(review);
        if(kind===0)hint(review,t("Tilldelningen gäller gruppen och dess ärvande undergrupper och resurser."));
        function summary(){
            if(!pick||!temporary||!start||!expiry)return;
            const subject=pick.selected(),bits=temporary.value==="temporary"?3:checks.filter(x=>x.input.checked).reduce((value,x)=>value|x.bit,existing?(metadata.checked?1:0):1);
            const path=groupPath(kind===0?target.id:target.groupId).map(x=>x.name).concat(kind===1?[target.name]:[]).join(" / ");
            reviewText.textContent=(subject?subject.name+(subject.samAccountName?" · "+subject.samAccountName:"")+" · "+subject.provider+" · "+subject.id:t("Sök och välj en användare eller grupp först."))+" — "+path+" — "+rights(bits)+" — "+(temporary.value==="temporary"?date(start.value)+" – "+date(expiry.value):temporary.value==="scheduled"?date(start.value)+" – "+t("Tills vidare"):t("Tills vidare"));
        }
        const refreshRights=()=>{permissions.hidden=preset.value!=="custom";summary();};
        preset.addEventListener("change",()=>{if(preset.value!=="custom"){if(metadata)metadata.checked=true;checks.forEach(x=>x.input.checked=!!(Number(preset.value)&x.bit));}refreshRights();});
        checks.forEach(x=>x.input.addEventListener("change",summary));start.addEventListener("input",summary);expiry.addEventListener("input",summary);
        const interval=()=>{const temp=temporary.value==="temporary";times.hidden=temporary.value==="permanent";start.required=!times.hidden;start.disabled=times.hidden;expiry.required=temp;expiry.disabled=!temp;expiry.hidden=!temp;expiry.labels[0].hidden=!temp;start.step=expiry.step="1";};
        temporary.addEventListener("change", () => { const temp = temporary.value === "temporary"; pick.onlyUsers(temp);interval();preset.disabled=temp;if(temp)preset.value="3";if(metadata){metadata.disabled=temp;if(temp)metadata.checked=true;} checks.forEach(x => { x.input.disabled = temp; if (temp) x.input.checked = x.bit === 2; });refreshRights(); });
        if(metadata)metadata.addEventListener("change",summary);interval();
        if(existing?.expiresAt){preset.disabled=true;if(metadata)metadata.disabled=true;checks.forEach(x=>x.input.disabled=true);}
        refreshRights();
        hint(body, t(existing?"Befintliga rättigheter bevaras tills du ändrar dem. Tillfällig rätt stoppar framtida utlämning men kan inte återkalla ett redan kopierat lösenord.":"Metadataåtkomst ingår. Vid tillfällig rätt är Från och Till obligatoriska. Tillfällig rätt stoppar framtida utlämning men kan inte återkalla ett redan kopierat lösenord."));
    }
    function ownerDialog(kind, target) {
        let pick; const body = dialog(t("Lägg till ägare · ") + target.name, t("Lägg till ägare"), async () => { const subject = pick(); await api("/owners", { targetKind: kind, targetId: target.id, subjectKind: subject.kind, provider: subject.provider, subjectId: subject.id }); await load(); }, {back:() => accessDialog(kind, target), afterSave:message => accessDialog(kind, target, message)});
        pick = directoryPicker(body); hint(body, t("Ägaren får hantera åtkomst inom resursen eller gruppens underträd."));
    }
    function moveDialog(kind, target, selectedDestination) {
        let destination; const body = dialog(t("Flytta · ") + target.name);
        const excluded=kind===0 ? groupIds(target.id,true) : new Set();
        const destinations=sortedGroups(state.groups.filter(x=>x.canManage&&!excluded.has(x.id)&&x.id!==(kind===0 ? target.parentId : target.groupId)));
        destination = select(body, t("Ny resursgrupp"), [["",t("Välj grupp")],...destinations.map(x=>[x.id,groupPath(x.id).map(g=>g.name).join(" / ")+" · "+x.id])],selectedDestination || ""); destination.required=true;
        const source=kind===0 ? groupPath(target.id).map(x=>x.name).join(" / ") : groupPath(target.groupId).map(x=>x.name).join(" / ")+" / "+target.name;
        hint(body,t("Från: {path}",{path:source}));
        hint(body,t("Flytten ändrar vilka grupptilldelningar som ärvs. Förhandsgranska skillnaden innan du bekräftar."));
        if(!destinations.length){hint(body,t("Ingen annan tillåten destinationsgrupp finns."));return;}
        body.append(button(t("Förhandsgranska åtkomst"), async () => {
            if(!destination.reportValidity())return;
            const epoch = ++dialogEpoch;
            const selected = destination.value; const [before,preview] = await Promise.all([api(`/access/${kind}/${target.id}`),api(`/move/${kind}/${target.id}/preview`, { destinationGroupId: selected, revision: target.revision })]);
            if (!canDisplay(epoch)) return;
            const summary = dialog(t("Bekräfta flytt · ") + target.name, t("Flytta"), async () => { await api(`/move/${kind}/${target.id}`, { destinationGroupId: selected, revision: target.revision }); await load(); },{
                back:()=>moveDialog(kind,target,selected),
                afterSave:async message=>{if(kind===1&&state.view==="groups")await navigate(navigation.url({view:"groups",groupId:selected,resourceId:target.id}));notice(message);}
            });
            hint(summary,t("Från: {path}",{path:source})); hint(summary,t("Till: {path}",{path:groupPath(selected).map(x=>x.name).join(" / ")}));
            hint(summary, t("Följande tilldelningar blir tillämpliga efter flytten. För en grupp påverkas även dess underträd; direkta tilldelningar längre ned bevaras."));
            const subjects={subjects:[...(before.subjects||[]),...(preview.subjects||[])]};
            for(const [label,grants,owners] of [
                ["Tillkommer",preview.grants.filter(x=>!before.grants.some(y=>y.id===x.id)),preview.owners.filter(x=>!before.owners.some(y=>y.id===x.id))],
                ["Försvinner",before.grants.filter(x=>!preview.grants.some(y=>y.id===x.id)),before.owners.filter(x=>!preview.owners.some(y=>y.id===x.id))],
                ["Behålls",preview.grants.filter(x=>before.grants.some(y=>y.id===x.id)),preview.owners.filter(x=>before.owners.some(y=>y.id===x.id))]
            ]) {
                const section=node("section"); section.append(node("h3",t(label)));summary.append(section);
                if(!grants.length&&!owners.length)hint(section,t("Ingen"));
                else table(section,[t("Identitet"),t("Rättigheter"),t("Gäller till")],[...grants.map(x=>[identityLabel(subjects,x),rights(x.permissions),date(x.expiresAt)]),...owners.map(x=>[identityLabel(subjects,x),t("Ägare"),"—"])]);
            }
            hint(summary,t("Förhandsgranskningen visar tilldelningar. Gruppmedlemskap kan ge ytterligare åtkomst. Servern kontrollerar aktuella rättigheter vid flytten."));
        }, "primary"));
    }
    function licenseTable(parent, licenses, scoped) {
        if (!licenses.length) { empty(parent, t("Inga licenser ännu"), t((scoped?has(4):(state.licenseResources||state.resources).some(x=>(x.permissions&5)===5))?"Registrera programlicenser, platser och giltighetstid.":"Det finns inga licenser här ännu. En person med ändringsrätt kan lägga till dem.")); return; }
        table(parent, [t("Produkt"), t("Platser"), t("Giltig till"), t("Åtgärder")], licenses.map(license => {
            const resource = (state.licenseResources||state.resources).find(x => x.id === license.resourceId); const actions = node("div", undefined, "actions");
            if (resource?.permissions & 2) actions.append(button(t("Visa nyckel"), () => revealLicense(license)));
            const secondary=[];
            if (resource?.permissions & 4) secondary.push(button(t("Ändra"), () => { state.resource = resource; licenseDialog(license); }));
            secondary.push(button(t("Tilldelningar"), () => assignmentDialog(license, resource)),button(t("Historik"), () => licenseHistory(license, resource)));
            if (resource?.permissions & 4) secondary.push(button(t("Ta bort"), () => {
                const body = dialog(t("Ta bort licens · ") + license.product, t("Ta bort"), async () => { await api(`/licenses/${license.id}/deleted`, { deleted: true, revision: license.revision }); await render();return t("Licensen finns i resursens papperskorg."); });
                hint(body,t("Resurs")+": "+(resource?(resource.path||groupPath(resource.groupId).map(x=>x.name).concat(resource.name).join(" / ")):license.resourceId));
                hint(body,t("Tilldelade platser: {count}",{count:license.assignedSeats}));
                body.append(node("p", t("Licensen flyttas till resursens papperskorg. Tilldelningar bevaras för återställning.")));
                if(resource?.canOpenResource!==false)body.append(routeLink(t("Öppna resursen"),{view:"vault",resourceId:license.resourceId}));
            }, "danger"));
            actions.append(actionMenu(t("Fler åtgärder"),secondary,scoped ? license.product : license.product+" · "+(resource?.name||"")));
            const product = node("div"); product.append(node("strong", license.product), node("p", scoped ? license.vendor : (resource?.name || "") + " · " + license.vendor, "hint"));
            if(!scoped&&resource?.canOpenResource===false)product.append(node("p",resource.path,"hint"));
            if(!scoped&&resource&&resource.canOpenResource!==false)product.append(routeLink((resource.path||groupPath(resource.groupId).map(x=>x.name).concat(resource.name).join(" / ")),{view:"vault",resourceId:resource.id}));
            const expiry = node("span", date(license.expiresAt), license.expiresAt && new Date(license.expiresAt) < new Date(Date.now() + 30 * 86400000) ? "badge warn" : "");
            return [product, `${license.assignedSeats} / ${license.seats}`, expiry, actions];
        })).classList.add("mobile-records");
    }
    async function licenseOverview() {
        const epoch = renderEpoch;
        state.licenseResources=state.session.isLicenseAdministrator?await api("/licenses/destinations"):state.resources;
        if(epoch!==renderEpoch)return;
        const writableResources = (state.licenseResources||state.resources).filter(x => (x.permissions & 5) === 5);
        heading(t("Licensöversikt"), t("Tilldelade platser och kommande utgångsdatum för dina synliga resurser."),
            [button(t("+ Lägg till licens"), () => {
                if(writableResources.length)licenseDialog(null,writableResources);
                else hint(dialog(t("Lägg till licens")),t("Ingen synlig resurs tillåter att du lägger till licenser. Be en resursägare om Metadata och Ändra för rätt destination."));
            }, "primary"),button(t("Papperskorg för licenser"),()=>licenseRecycleDialog(writableResources))]);
        const resume=state.licenseResume;state.licenseResume=null;
        const licenses=resume?.licenses.slice()||[];let offset=resume?.offset||0,hasMore=true,loadError;
        try{
            for(let page=0;page<100&&hasMore&&offset<20000;page++){
                const result=await api(`/licenses/overview?offset=${offset}`);if(epoch!==renderEpoch)return;
                licenses.push(...result.items.filter(item=>!licenses.some(existing=>existing.id===item.id)));offset+=result.items.length;hasMore=result.hasMore;
                if(hasMore&&!result.items.length)throw new Error(t("Licensöversikten kunde inte läsas färdigt."));
            }
        }catch(error){if(epoch!==renderEpoch)return;if(!licenses.length)throw error;loadError=error.message;}
        if (epoch !== renderEpoch) return;
        if(hasMore){const partial=node("section",undefined,"notice error");partial.append(node("p",t("Ofullständig översikt: sammanfattningar, sökning och sortering gäller bara inlästa licenser.")));if(loadError){partial.append(node("p",loadError),button(t("Försök igen"),()=>{if(epoch!==renderEpoch)return;state.licenseResume={licenses,offset};return render();}));}else partial.append(routeLink(t("← Alla resurser"),{view:"vault"}));$("content").append(partial);}
        const expiring=x=>x.expiresAt&&new Date(x.expiresAt)<new Date(Date.now()+30*86400000);
        const stats=node("div",undefined,"summary-grid"),toolbar=node("div",undefined,"toolbar"); $("content").append(stats,toolbar);
        const search=field(controlGroup(toolbar),t("Sök licenser"),"search",state.licenseFilters.search||"");
        const filter=select(controlGroup(toolbar),t("Licensfilter"),[["all",t("Alla")],["assigned",t("Med tilldelade platser")],["expiring",t("Utgångna eller förfaller inom 30 dagar")],["full",t("Inga lediga platser")]],state.licenseFilters.filter||"all");
        const sort=select(controlGroup(toolbar),t("Sortera"),[["product",t("Produkt")],["expiry",t("Giltig till")],["available",t("Lediga platser")],["resource",t("Resurs")]],state.licenseFilters.sort||"product");
        const section=panel($("content"),t("Programlicenser")),count=node("p",undefined,"hint"),results=node("div");count.setAttribute("role","status");section.append(count,results);
        const summaries=[];
        const paint=()=>{
            state.licenseFilters={search:search.value,filter:filter.value,sort:sort.value};
            summaries.forEach(([control,value])=>control.setAttribute("aria-pressed",String(filter.value===value)));
            const resourceName=x=>{const resource=(state.licenseResources||state.resources).find(r=>r.id===x.resourceId);return resource?.path||resource?.name||"";};
            const visible=licenses.filter(x=>(filter.value!=="assigned"||x.assignedSeats>0)&&(filter.value!=="expiring"||expiring(x))&&(filter.value!=="full"||x.assignedSeats>=x.seats)
                &&[x.product,x.vendor,x.purchaseReference,resourceName(x)].join(" ").toLocaleLowerCase(i18n.locale).includes(search.value.trim().toLocaleLowerCase(i18n.locale)));
            visible.sort((a,b)=>(sort.value==="expiry"?(a.expiresAt?Date.parse(a.expiresAt):Infinity)-(b.expiresAt?Date.parse(b.expiresAt):Infinity):sort.value==="available"?(a.seats-a.assignedSeats)-(b.seats-b.assignedSeats):sort.value==="resource"?resourceName(a).localeCompare(resourceName(b),i18n.locale):0)||a.product.localeCompare(b.product,i18n.locale)||a.id.localeCompare(b.id));
            count.textContent=t("{shown} av {total} licenser visas",{shown:visible.length,total:licenses.length});results.replaceChildren();
            if(!visible.length&&licenses.length)empty(results,t("Inga matchande licenser"),t("Ändra eller rensa filtren."));else licenseTable(results,visible,false);
        };
        for(const [value,number,label] of [["all",licenses.length,t("Registrerade licenser")],["assigned",licenses.reduce((a,x)=>a+x.assignedSeats,0),t("Tilldelade platser")],["expiring",licenses.filter(expiring).length,t("Utgångna eller förfaller inom 30 dagar")]]){
            const control=button(number+" · "+label,()=>{filter.value=value;paint();},"summary-card");stats.append(control);summaries.push([control,value]);
        }
        search.addEventListener("input",paint);filter.addEventListener("change",paint);sort.addEventListener("change",paint);
        toolbar.append(button(t("Rensa filter"),()=>{search.value="";filter.value="all";paint();}));paint();
    }
    function licenseRecycleDialog(destinations,selected=""){
        let picker;
        const body=dialog(t("Papperskorg för licenser"));
        picker=select(body,t("Resurs"),[["",t("Välj en destinationsresurs")],...destinations.map(x=>[x.id,x.path||groupPath(x.groupId).map(g=>g.name).concat(x.name).join(" / ")])],selected);
        const results=node("div");body.append(button(t("Öppna"),async()=>{
            if(!picker.value)return;
            const id=picker.value,epoch=dialogEpoch;results.replaceChildren(node("p",t("Läser in…")));
            const rows=await api(`/resources/${id}/deleted-licenses`);if(!canDisplay(epoch))return;results.replaceChildren();
            if(!rows.length)hint(results,t("Papperskorgen är tom."));
            else table(results,[t("Produkt"),t("Platser"),t("Åtgärder")],rows.map(x=>[x.product,String(x.assignedSeats)+" / "+x.seats,button(t("Återställ"),()=>{
                const confirmation=dialog(t("Återställ borttagen licens"),t("Återställ"),async()=>{await api(`/licenses/${x.id}/deleted`,{deleted:false,revision:x.revision});await render();},{back:()=>licenseRecycleDialog(destinations,id)});
                hint(confirmation,x.product+" · "+(destinations.find(r=>r.id===id)?.path||destinations.find(r=>r.id===id)?.name||id));
            })]));
        }),results);
    }
    function licenseDialog(license, writableResources) {
            const resourceId = license?.resourceId || state.resource?.id;
            const originalExpiry=license?.expiresAt||null,originalDate=originalExpiry?calendarDate(originalExpiry):"";
            let resourcePicker;
            let product, vendor, reference, seats, expiry, key, notes, change;
            const body = dialog(license ? t("Ändra licens") : t("Lägg till licens"), t(license?"Spara":"Lägg till licens"), async () => {
                const mode = license ? Number(change.value) : 1;
                if (mode === 1 && !key.value.trim()) throw new Error(t("Ange en ny licensnyckel eller välj att behålla eller tömma nyckeln."));
                await api(license ? `/licenses/${license.id}/update` : "/licenses", { resourceId: resourcePicker ? resourcePicker.value : resourceId, product: product.value, vendor: vendor.value, purchaseReference: reference.value,
                    seats: Number(seats.value), expiresAt: expiry.value===originalDate?originalExpiry:expiry.value ? new Date(expiry.value + "T23:59:59").toISOString() : null,
                    secretChange: mode, payload: mode === 1 ? { licenseKey: key.value, notes: notes.value } : null, revision: license?.revision || 0 }); await render();
            });
            if (writableResources) { resourcePicker = select(body, t("Resurs"), [["",t("Välj en destinationsresurs")],...writableResources.map(x => [x.id,(x.path||groupPath(x.groupId).map(g=>g.name).concat(x.name).join(" / "))+" · "+x.id])],""); resourcePicker.required=true; }
            product = field(body, t("Produkt"), "text", license?.product || "", true);
            seats = field(body, t("Antal platser"), "number", String(license?.seats || 1), true); seats.min = "1"; seats.max = "1000000";
            if(license)change = select(body, t("Licensnyckel och hemliga anteckningar"), [["0", t("Behåll befintliga värden")], ["1", t("Ersätt med nya värden")], ["2", t("Töm värdena uttryckligen")]], "0");
            key = field(body, t(license?"Ny licensnyckel":"Licensnyckel"), "password","",!license);
            notes = field(body, t(license?"Nya hemliga anteckningar":"Hemliga anteckningar"), "textarea"); key.maxLength = notes.maxLength = 65536;
            const commercial=node("details");commercial.append(node("summary",t("Leverantör, inköp och giltighet (valfritt)")));commercial.open=!!license;body.append(commercial);
            vendor = field(commercial, t("Leverantör"), "text", license?.vendor || "");
            reference = field(commercial, t("Inköpsreferens"), "text", license?.purchaseReference || "");
            expiry = field(commercial, t("Giltig till"), "date", originalDate);
            hint(commercial,t("Datum visas i din lokala tidszon. Oförändrat datum behåller den exakta giltighetstiden; ett nytt datum gäller till dagens slut i din tidszon."));
            if(license){
                const update = () => { key.value = notes.value = ""; key.disabled = notes.disabled = change.value !== "1"; key.required = change.value === "1";key.labels[0].textContent=t(change.value==="1"?"Ny licensnyckel (obligatoriskt)":"Ny licensnyckel"); updateSecretSubmitState(); };
                change.addEventListener("change", update); update();
                hint(body, t("Metadata kan ändras utan att läsa nyckeln. Ersättning och tömning skapar en ny krypterad version; tidigare värden bevaras i historiken."));
            }else hint(body,t("Licensnyckeln sparas krypterad. Läsrätt krävs för att visa den efteråt."));
    }
    async function revealLicense(license, version) {
        const epoch = ++dialogEpoch;
        const payload = await api(`/licenses/${license.id}/reveal`, { version: version ?? null });
        if (!canDisplay(epoch)) return;
        const body = dialog(t("Licensnyckel · ") + license.product + (version ? ` · version ${version}` : ""),undefined,undefined,version?{back:()=>licenseHistory(license,(state.licenseResources||state.resources).find(x=>x.id===license.resourceId))}:{});
        revealedDialog = true;
        const key = field(body, t("Licensnyckel"), "textarea", payload.licenseKey); key.readOnly = true;
        const notes = field(body, t("Anteckningar"), "textarea", payload.notes); notes.readOnly = true;
        hint(body, t("Visningen är auditerad. Fälten töms efter 30 sekunder eller när sidan tappar fokus."));revealCountdown(body);
    }
    function versionCreator(version){const label=node('div');label.append(node('strong',(version.createdByName||version.createdBy)+(version.samAccountName?' · '+version.samAccountName:'')));if(version.createdByName){const details=node('details');details.append(node('summary',t('Registrerad identitet')),node('p',version.createdBy,'audit-id'));label.append(details);}return label;}
    async function licenseHistory(license, resource) {
        const loaded=await metadataDialog(t("Versionshistorik · ")+license.product,()=>api(`/licenses/${license.id}/versions`),()=>licenseHistory(license,resource));if(!loaded)return;
        const {body,value:versions}=loaded;
        table(body, ["Version", t("Datum"), t("Ändrad av"), t("Åtgärder")], versions.map(version => {
            const actions = node("div", undefined, "actions");
            if (resource?.permissions & 2) actions.append(button(t("Visa"), () => revealLicense(license, version.version)));
            if ((resource?.permissions & 6) === 6 && version.version !== license.currentVersion) actions.append(button(t("Återställ"), () => {
                const confirmation = dialog(t("Återställ licensversion"), t("Återställ"), async () => { await api(`/licenses/${license.id}/restore`, { version: version.version, revision: license.revision }); await render(); },{back:()=>licenseHistory(license,resource)});
                hint(confirmation, t("Version {version} sparas som en ny version. Nuvarande metadata behålls.", {version: version.version}));
            }));
            return [String(version.version), date(version.createdAt), versionCreator(version), actions];
        }));
    }
    async function assignmentDialog(license, resource,feedback) {
        resource=(state.licenseResources||state.resources).find(x=>x.id===license.resourceId);
        const loaded=await metadataDialog(t("Tilldelningar · ")+license.product,async()=>{
            const [rows,licenses]=await Promise.all([api(`/licenses/${license.id}/assignments`),api(`/resources/${license.resourceId}/licenses`)]);
            const current=licenses.find(x=>x.id===license.id);if(!current)throw new Error(t("Licensen är inte tillgänglig."));return{rows,current};
        },()=>assignmentDialog(license,resource));if(!loaded)return;
        const {body,value:{rows:assignments,current}}=loaded;license=current;
        const assigned=assignments.reduce((sum,x)=>sum+x.seats,0),available=Math.max(0,license.seats-assigned);
        if(feedback){const result=node("p",feedback,"notice");result.setAttribute("role","status");body.append(result);}
        hint(body, t("{assigned} av {seats} platser tilldelade.", {assigned,seats:license.seats}));
        const returnToOverview=()=>assignmentDialog(license,resource);
        if (resource?.permissions & 4) {const add=button(t("+ Tilldela platser"), () => {
            let mode, pick, target, seats; const form = dialog(t("Tilldela licensplatser"), t("Tilldela platser"), async () => {
                if(mode.value==="resource"&&!target.value)throw new Error(t("Välj en destinationsresurs"));
                const subject = mode.value === "user" ? pick() : null;
                await api("/assignments", { licenseId: license.id, resourceId: mode.value === "resource" ? target.value : null,
                    userProvider: subject?.provider || null, userId: subject?.id || null, seats: Number(seats.value) }); await load();
            },{back:returnToOverview,afterSave:message=>assignmentDialog(license,resource,message)});
            hint(form,license.product+" · "+(resource?(resource.path||groupPath(resource.groupId).map(g=>g.name).concat(resource.name).join(" / ")):""));
            hint(form,t("{available} platser tillgängliga.",{available}));
            mode = select(form, t("Tilldela till"), [["user", t("Användare")], ["resource", t("Resurs")]]);
            const users = node("div"); pick = directoryPicker(users,{searchPath:`/licenses/${license.id}/people`,usersOnly:true});form.append(users);
            const resources = node("div"); target = select(resources, t("Resurs"), [["",t("Välj en destinationsresurs")],...(state.licenseResources||state.resources).filter(x=>x.permissions&1).map(x=>[x.id,(x.path||groupPath(x.groupId).map(g=>g.name).concat(x.name).join(" / "))+" · "+x.id])],"");form.append(resources);resources.hidden=true;target.disabled=true;
            mode.addEventListener("change",()=>{pick.clear();target.value="";users.hidden=mode.value!=="user";users.querySelector("fieldset").disabled=users.hidden;resources.hidden=mode.value!=="resource";target.disabled=mode.value!=="resource";target.required=!target.disabled;});
            seats = field(form, t("Antal platser"), "number", "1", true); seats.min = "1"; seats.max = String(available);
        });add.disabled=!available;body.append(add);if(!available)hint(body,t("Alla platser är tilldelade. Återta en tilldelning innan du lägger till en ny."));}
        table(body, [t("Tilldelad till"), t("Platser"), t("Datum"), ""], assignments.map(x => {
            const target=(state.licenseResources||state.resources).find(r=>r.id===x.resourceId),label=x.destinationExcluded?t("Undantagen destination"):x.userId?(x.userName||x.userId)+(x.samAccountName?" · "+x.samAccountName:""):(target?(target.path||groupPath(target.groupId).map(g=>g.name).concat(target.name).join(" / ")):x.resourceId);
            const identity=node("div");identity.append(node("strong",label),node("p",x.userId?(x.userProvider+" · "+x.userId):x.resourceId,"hint audit-id"));
            return[identity,String(x.seats),date(x.createdAt),!x.destinationExcluded&&(resource?.permissions&4)?button(t("Återta platser"),()=>{
                const confirmation=dialog(t("Bekräfta återtagning"),t("Återta platser"),async()=>{await api(`/assignments/${x.id}/remove`,{});await load();},{back:returnToOverview,afterSave:message=>assignmentDialog(license,resource,message)});
                hint(confirmation,t("Återta {seats} platser från {recipient}?",{seats:x.seats,recipient:label}));
            },"danger"):""];
        })).classList.add("mobile-records");if(!assignments.length)hint(body,t("Inga tilldelningar ännu."));
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
        const advanced=node("details");advanced.append(node("summary",t("Avancerade filter")));const technicalFilters=node("div",undefined,"audit-filters");advanced.append(technicalFilters);
        const filter = (label, type, parent=toolbar) => { const group = node("div"); parent.append(group); return field(group, label, type); };
        const from = filter(t("Från"), "datetime-local"), to = filter(t("Till (exklusive)"), "datetime-local");
        const actor = filter(t("Aktörens ID"), "text",technicalFilters), action = filter(t("Operation"), "text",technicalFilters); $("content").append(toolbar,advanced);
        const actorChoices=targets.actors||[],actionChoices=targets.actions||[];
        const actorChoice=select(controlGroup(toolbar),t("Person eller tjänst"),[["",t("Välj registrerad aktör")],...actorChoices.map((x,index)=>[String(index+1),(x.name||x.id)+" · "+x.provider+" · "+x.id])]);
        const operationChoice=select(controlGroup(toolbar),t("Åtgärd"),[["",t("Välj registrerad operation")],...actionChoices.map(code=>[code,auditActionLabel(code)])]);
        actorChoice.disabled=operationChoice.disabled=targetsUnavailable;
        hint(advanced,t("Aktörsnamn är aktuella katalognamn, inte historiska namn. Högst 100 registrerade aktörer visas; ID och operationskod kan anges manuellt."));
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
        let actorProvider=saved.actorProvider||"";
        const read=()=>({from:from.value,to:to.value,actor:actor.value,actorProvider,action:action.value,resource:resource.value,group:group.value,descendants:descendants.checked});
        let applied=saved.applied?{...saved.applied}:{...read(),offset:saved.offset||0},paintEpoch=0,hasResults=false,rowCount=0,snapshotExpired=false;
        const selectedActor=actorChoices.findIndex(x=>x.id===actor.value&&x.provider===actorProvider);
        actorChoice.value=selectedActor<0?"":String(selectedActor+1);operationChoice.value=action.value;
        advanced.open=!!(actor.value&&selectedActor<0||action.value&&!actionChoices.includes(action.value));
        const panelNode = panel($("content"), t("Händelser")); const results = node("div"),failure=node("div"),dirty=node("p",undefined,"hint");panelNode.append(dirty,results,failure);
        const remember=()=>{state.auditFilters={...read(),offset:applied.offset,applied:{...applied}};dirty.textContent=t("Ändrade filter är inte tillämpade. Listan och exporten använder senast tillämpade filter.");
            dirty.hidden=Object.entries(read()).every(([key,value])=>applied[key]===value);};
        actor.addEventListener("input",()=>{actorProvider="";actorChoice.value="";remember();});
        action.addEventListener("input",()=>{operationChoice.value=action.value;remember();});
        actorChoice.addEventListener("change",()=>{const selected=actorChoices[Number(actorChoice.value)-1];actor.value=selected?.id||"";actorProvider=selected?.provider||"";remember();});
        operationChoice.addEventListener("change",()=>{action.value=operationChoice.value;remember();});
        [from,to,resource,group,descendants].forEach(input=>{input.addEventListener("input",remember);input.addEventListener("change",remember);});
        const count = node("p", undefined, "hint"); count.setAttribute("role", "status"); panelNode.append(count);
        const snapshotInfo=node("p",undefined,"hint filter-actions");toolbar.append(snapshotInfo);
        hint(panelNode,t("En ändring kan ha flera auditrader: begäran och genomförande är steg i samma operation, inte två genomförda ändringar. En begäran bevisar inte att ändringen slutfördes. Jämför operations-ID i detaljer; andra steg kan ligga utanför sidan eller filtret."));
        function query(value) { const params = new URLSearchParams({offset:value.offset});if(value.snapshotId)params.set("snapshotId",value.snapshotId);if(value.from)params.set("from",new Date(value.from).toISOString());if(value.to)params.set("to",new Date(value.to).toISOString());if(value.actor)params.set("actorId",value.actor);if(value.actorProvider)params.set("actorProvider",value.actorProvider);if(value.action)params.set("action",value.action);if(value.resource)params.set("resourceId",value.resource);if(value.group){params.set("groupId",value.group);params.set("includeSubgroups",String(value.descendants));}return params.toString(); }
        const availability=busy=>{previous.disabled=busy||snapshotExpired||!hasResults||applied.offset===0;next.disabled=busy||snapshotExpired||!hasResults||rowCount<200||applied.offset>=1000000;exportPage.disabled=busy||snapshotExpired||!hasResults||!applied.snapshotId;apply.disabled=refreshAudit.disabled=busy;};
        const paint = async requested => {
            const requestEpoch = ++paintEpoch;availability(true);failure.replaceChildren();results.setAttribute("aria-busy","true");count.textContent=t("Läser in…");
            try {
            const events = await api("/audit?" + query(requested)); if (epoch !== renderEpoch || requestEpoch !== paintEpoch) return;
            applied={...requested,snapshotId:events.snapshotId||requested.snapshotId};snapshotExpired=false;hasResults=true;rowCount=events.length;remember();results.replaceChildren();
            snapshotInfo.textContent=events.capturedAt?t("Auditvy från {captured}, giltig till {expires}. Signaturer verifierades när vyn lästes. Uppdatera auditvyn för nya händelser.",{captured:date(events.capturedAt),expires:date(events.expiresAt)}):t("Auditvyn saknar en stabil ögonblicksbild. Uppdatera innan export.");
            count.textContent = events.length + t(" händelser visas på sida ") + (applied.offset / 200 + 1) + ".";
            if (!events.length) { empty(results, t("Inga händelser"), t("Ändra filtret eller gå till föregående sida.")); return; }
            table(results, [t("Tid"), t("Aktör"), t("Operation"), t("Resurs / resursgrupp"), t("Resultat"), t("Verifiering"),t("Tekniska detaljer")], events.map(x => {
                const actor=x.signatureValid?actorChoices.find(a=>a.id===x.event.actorId&&a.provider===x.event.actorProvider):null;
                const outcome=node("div"),phases={0:"Händelse",1:"Begäran",2:"Genomförd",3:"Misslyckad",Event:"Händelse",Intent:"Begäran",Committed:"Genomförd",Failed:"Misslyckad"};
                const outcomes={Requested:"Begärd",Committed:"Bekräftad ändring",CommitUncertain:"Kan ha sparats. Kontrollera destinationen före ett nytt försök.",Success:"Lyckades",Denied:"Nekad",Failed:"Misslyckad",InvalidSignature:"Ogiltig signatur"};
                outcome.append(node("strong",x.signatureValid?t(Object.hasOwn(phases,x.event.phase)?phases[x.event.phase]:"Händelse"):t("Ej verifierat")));if(x.signatureValid)outcome.append(node("p",t(Object.hasOwn(outcomes,x.event.outcome)?outcomes[x.event.outcome]:x.event.outcome),"hint"));
                const details=node("details");details.append(node("summary",t("Tekniska detaljer")));
                const facts=[["ID",x.event.id],[t("Operations-ID"),x.event.operationId],[t("Korrelations-ID"),x.event.correlationId],[t("Aktör"),[x.event.actorProvider,x.event.actorId].filter(Boolean).join(" · ")],[t("Operation"),x.event.action],[t("Steg"),x.event.phase],[t("Resultat"),x.event.outcome]];
                facts.forEach(([label,value])=>details.append(node("p",label+": "+String(value??"—"),"audit-id")));
                return [date(x.event.timestamp),node("span",actor?.name||x.event.actorId),auditActionLabel(x.event.action),auditScopeLabel(x),outcome,node("span",x.signatureValid?t("Giltig signatur"):t("Ogiltig signatur"),"badge"+(x.signatureValid?"":" error")),details];
            }));
            }catch(error){if(epoch!==renderEpoch||requestEpoch!==paintEpoch)return;if(error.status===409)snapshotExpired=true;count.textContent=hasResults?t("Tidigare resultat visas. Uppdateringen misslyckades."):t("Inga resultat har lästs ännu.");failure.append(node("p",error.message),button(t(error.status===409?"Uppdatera auditvyn":"Försök igen"),()=>paint(error.status===409?{...requested,snapshotId:undefined,offset:0}:requested)));}
            finally{if(epoch===renderEpoch&&requestEpoch===paintEpoch){results.removeAttribute("aria-busy");availability(false);}}
        };
        const filterActions = node("div", undefined, "actions filter-actions"); toolbar.append(filterActions);
        const apply=button(t("Filtrera"),()=>{if(from.value&&to.value&&new Date(from.value)>=new Date(to.value))throw new Error(t("Sluttiden måste vara efter starttiden."));return paint({...read(),offset:0});});
        const exportPage=button(t("Exportera denna sida"), async () => {
            const response = await fetch("/api/audit/export?" + query(applied), { credentials: "same-origin", cache: "no-store", headers: {"X-Valvra-Language": i18n.language} }); if (!response.ok){if(response.status===409){snapshotExpired=true;availability(false);}throw new Error(t(response.status===409?"Auditvyn har löpt ut. Läs aktuella händelser innan du fortsätter.":"Auditexporten misslyckades."));}
            const url = URL.createObjectURL(await response.blob()); const link = node("a"); link.href = url; link.download = "valvra-audit.json"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        });const refreshAudit=button(t("Uppdatera auditvyn"),()=>paint({...applied,snapshotId:undefined,offset:0}));filterActions.append(apply,exportPage,refreshAudit);
        const previous=button(t("← Föregående"),()=>paint({...applied,offset:Math.max(0,applied.offset-200)})),next=button(t("Nästa →"),()=>paint({...applied,offset:applied.offset+200}));
        const paging = node("div", undefined, "actions");paging.append(previous,next);$("content").append(paging);await paint(applied);
    }
    function auditActionLabel(code){const labels={"Access.Update":"Ändra åtkomst","Access.Exclude":"Undanta åtkomst","Access.Restore":"Återställ åtkomst","Group.Delete":"Ta bort resursgrupp","Resource.Delete":"Ta bort resurs","Owner.Add":"Lägg till ägare","Owner.Remove":"Ta bort ägare","GlobalRoles.Update":"Ändra globala roller","Secret.Undelete":"Återställ borttaget lösenord","License.Delete":"Ta bort licens","License.Undelete":"Återställ borttagen licens","Group.Create":"Skapa resursgrupp","Group.Rename":"Byt namn på resursgrupp","Group.Move":"Flytta resursgrupp","Resource.Create":"Skapa resurs","Resource.Rename":"Byt namn på resurs","Resource.Move":"Flytta resurs","Session.Identified":"Verifiera inloggning","Secret.Create":"Skapa lösenord","Secret.Update":"Ändra lösenord","Secret.Reveal":"Visa lösenord","Secret.Copy":"Kopiera lösenord","Secret.Delete":"Ta bort lösenord","Secret.RestoreRead":"Läs version för återställning","License.Create":"Lägg till licens","License.Update":"Ändra licens","License.Reveal":"Visa licensnyckel","License.Assign":"Tilldela licensplatser","License.Unassign":"Ta bort platstilldelning","Access.Grant":"Tilldela behörighet","Access.Revoke":"Återkalla behörighet","Access.Denied":"Åtkomst nekad","Audit.Export":"Exportera auditlogg"};return Object.hasOwn(labels,code)?t(labels[code]):code;}
    document.querySelectorAll("[data-view],.brand").forEach(bindRouteLink);
    window.addEventListener("resize",()=>closeActionMenu?.());
    // Scrolling a menu trigger into view can dispatch a delayed scroll after it
    // opens. Re-anchor the popup instead of closing it during that interaction.
    window.addEventListener("scroll",event=>{
        if(!actionMenuElement||event.target.closest?.(".action-menu-popup"))return;
        const trigger=actionMenuElement.querySelector("button"),menu=actionMenuElement.querySelector(".action-menu-popup");
        const rect=trigger.getBoundingClientRect(),size=menu.getBoundingClientRect();
        if(rect.bottom<0||rect.top>window.innerHeight){closeActionMenu?.();return;}
        menu.style.left=Math.max(8,Math.min(rect.right-size.width,window.innerWidth-size.width-8))+"px";
        menu.style.top=Math.max(8,rect.bottom+size.height+8<=window.innerHeight?rect.bottom+4:rect.top-size.height-4)+"px";
    },true);
    document.addEventListener("pointerdown",event=>{if(actionMenuElement&&!actionMenuElement.contains(event.target))closeActionMenu?.();});
    window.addEventListener("popstate", () => {
        if (routeReady && window.location.pathname !== activePath) safely(() => navigate(window.location.pathname, { traversal: true }));
    });
    $("close-dialog").addEventListener("click", dismissDialog); $("dialog").addEventListener("cancel", event => { event.preventDefault(); dismissDialog(); });
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
    async function start(){
        await i18n.ready;const progress=node("p",t("Läser in…"));progress.setAttribute("role","status");$("content").replaceChildren(progress);$("content").setAttribute("aria-busy","true");
        try{await refreshSession();await load();}
        catch(error){heading(t("Vyn kunde inte läsas"),t("Försök igen för att kontrollera din session och läsa aktuell metadata."));$("content").replaceChildren(node("p",error.message),button(t("Försök igen"),start,"primary"));}
        finally{$("content").removeAttribute("aria-busy");}
    }
    safely(async () => { await i18n.ready; await start(); });
})();
