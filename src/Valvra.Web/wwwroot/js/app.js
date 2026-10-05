"use strict";
(() => {
    const $ = id => document.getElementById(id);
    const state = { session: null, groups: [], resources: [], profiles: [], view: "vault", resource: null };
    let secretTimer;
    let lastActivity = Date.now();
    const node = (tag, text, className) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (className) n.className = className; return n; };
    const button = (text, action, style = "") => { const n = node("button", text, "button " + style); n.type = "button"; n.addEventListener("click", () => safely(action)); return n; };
    const has = bit => !!(state.resource && (state.resource.permissions & bit));
    const date = value => value ? new Date(value).toLocaleString("sv-SE") : "—";
    const rights = value => [[1, "Metadata"], [2, "Läsa"], [4, "Ändra"], [8, "Behörigheter"], [16, "LDAP-test"]].filter(([bit]) => value & bit).map(([, name]) => name).join(", ");
    function notice(message, error = false) { $("notice").textContent = message; $("notice").className = "notice" + (error ? " error" : ""); $("notice").hidden = false; }
    async function safely(action) { try { await action(); } catch (error) { notice(error.message || "Operationen misslyckades.", true); } }
    async function api(path, body) {
        const options = { credentials: "same-origin", cache: "no-store", headers: { "Accept": "application/json" } };
        if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-CSRF-TOKEN"] = state.session.csrfToken; options.body = JSON.stringify(body); }
        const response = await fetch("/api" + path, options);
        if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.error || (response.status === 401 ? "Windows-inloggningen kunde inte verifieras." : "Operationen kunde inte slutföras.")); }
        return response.status === 204 ? null : response.json();
    }
    function clearSecrets() {
        clearTimeout(secretTimer);
        $("dialog-content").querySelectorAll("input,textarea").forEach(field => { if (field.type === "password" || field.dataset.sensitive) field.value = ""; });
    }
    function closeDialog() { clearSecrets(); $("dialog").close(); $("dialog-content").replaceChildren(); $("dialog-actions").replaceChildren(); }
    function dialog(title, submitLabel, submit) {
        closeDialog(); $("dialog-title").textContent = title; $("dialog-error").textContent = "";
        const content = $("dialog-content");
        const actions = $("dialog-actions"); actions.append(button("Avbryt", closeDialog));
        if (submit) { const save = node("button", submitLabel, "button primary"); save.type = "submit"; actions.append(save); }
        $("dialog-form").onsubmit = async event => {
            event.preventDefault(); if (!submit) return;
            const controls = Array.from(actions.querySelectorAll("button")); controls.forEach(x => x.disabled = true);
            try { const message = await submit(); closeDialog(); notice(typeof message === "string" ? message : "Ändringen har sparats och auditerats."); }
            catch (error) { $("dialog-error").textContent = error.message; }
            finally { controls.forEach(x => x.disabled = false); }
        };
        $("dialog").showModal(); return content;
    }
    function field(parent, label, type = "text", value = "", required = false) {
        const id = "field-" + crypto.randomUUID(); const l = node("label", label); l.htmlFor = id;
        const input = type === "textarea" ? node("textarea") : node("input"); input.id = id;
        if (type !== "textarea") input.type = type; input.value = value ?? ""; input.required = required; input.autocomplete = "off";
        if (type === "password" || type === "textarea") input.dataset.sensitive = "true";
        parent.append(l, input); return input;
    }
    function select(parent, label, items, selected) {
        const id = "field-" + crypto.randomUUID(); const l = node("label", label); l.htmlFor = id; const s = node("select"); s.id = id;
        items.forEach(([value, text]) => { const o = node("option", text); o.value = value; s.append(o); });
        if (selected !== undefined && selected !== null) s.value = selected; parent.append(l, s); return s;
    }
    function hint(parent, text) { parent.append(node("p", text, "hint")); }
    function empty(parent, title, text) { const box = node("div", undefined, "empty"); box.append(node("h2", title), node("p", text)); parent.append(box); }
    function heading(title, description, actions = []) { $("page-title").textContent = title; $("page-description").textContent = description; $("page-actions").replaceChildren(...actions); }
    function table(parent, headers, rows) {
        const wrap = node("div", undefined, "table-wrap"); const t = node("table"); const head = node("thead"); const tr = node("tr");
        headers.forEach(x => tr.append(node("th", x))); head.append(tr); t.append(head); const body = node("tbody");
        rows.forEach(row => { const r = node("tr"); row.forEach(value => { const cell = node("td"); cell.append(value instanceof Node ? value : document.createTextNode(value ?? "")); r.append(cell); }); body.append(r); });
        t.append(body); wrap.append(t); parent.append(wrap); return t;
    }
    function panel(parent, title, actions = []) { const p = node("section", undefined, "panel"); const top = node("div", undefined, "panel-heading"); top.append(node("h2", title)); const a = node("div", undefined, "actions"); a.append(...actions); top.append(a); p.append(top); parent.append(p); return p; }
    async function load() {
        [state.groups, state.resources, state.profiles] = await Promise.all([api("/groups"), api("/resources"), api("/ldap-profiles")]);
        if (state.resource) state.resource = state.resources.find(x => x.id === state.resource.id) || null;
        await render();
    }
    async function render() {
        $("content").replaceChildren(); $("breadcrumb").textContent = { vault: "Resurser", groups: "Resursgrupper", licenses: "Licensöversikt", audit: "Auditlogg" }[state.view];
        document.querySelectorAll("[data-view]").forEach(x => x.classList.toggle("active", x.dataset.view === state.view));
        if (state.view === "vault") { if (state.resource) await resourcePage(); else resourcesPage(); }
        else if (state.view === "groups") groupsPage();
        else if (state.view === "licenses") await licenseOverview();
        else await auditPage();
    }
    function resourcesPage() {
        heading("Dina resurser", "Lösenord och licenser samlade kring resurserna de tillhör.", state.groups.some(x => x.canManage) ? [button("+ Ny resurs", createResource, "primary")] : []);
        const content = $("content");
        if (!state.resources.length) { empty(content, "Inga resurser ännu", state.session.isAccessAdministrator ? "Skapa en resursgrupp, lägg till resurser och tilldela åtkomst för att öppna valvet." : "Be en resursägare eller behörighetsadministratör tilldela dig åtkomst."); return; }
        const toolbar = node("div", undefined, "toolbar"); const search = node("input"); search.placeholder = "Sök bland dina resurser…"; search.setAttribute("aria-label", "Sök resurser"); toolbar.append(search);
        const filter = select(toolbar, "Resursgrupp", [["", "Alla grupper"], ...state.groups.map(x => [x.id, x.name])]); content.append(toolbar);
        const cards = node("div", undefined, "cards"); content.append(cards);
        const paint = () => {
            cards.replaceChildren();
            const resources = state.resources.filter(x => x.name.toLowerCase().includes(search.value.toLowerCase()) && (!filter.value || x.groupId === filter.value));
            resources.forEach(resource => {
                const card = node("article", undefined, "card"); const top = node("div", undefined, "card-top"); top.append(node("div", "◇", "resource-icon"), node("span", resource.canManage ? "Administrerar" : "Tilldelad åtkomst", "badge"));
                card.append(top, node("h3", resource.name), node("p", state.groups.find(x => x.id === resource.groupId)?.name || "Resursgrupp"));
                const actions = node("div", undefined, "actions"); actions.append(button("Öppna resurs →", async () => { state.resource = resource; await render(); })); card.append(actions); cards.append(card);
            });
            if (!resources.length) empty(cards, "Inga träffar", "Prova ett annat sökord eller en annan grupp.");
        }; search.addEventListener("input", paint); filter.addEventListener("change", paint); paint();
    }
    function groupsPage() {
        const canCreate = state.session.isAccessAdministrator || state.groups.some(x => x.canManage);
        heading("Resursgrupper", "Organisera resurser i ett träd. Tilldelningar ärvs till undergrupper och resurser.", canCreate ? [button("+ Ny grupp", createGroup, "primary")] : []);
        if (!state.groups.length) { empty($("content"), "Inga synliga grupper", "Grupper visas när du har metadataåtkomst eller rätt att administrera dem."); return; }
        const p = panel($("content"), "Gruppstruktur");
        const depth = group => { let d = 0, g = group; const visited = new Set(); while (g.parentId && d < 128 && !visited.has(g.id)) { visited.add(g.id); g = state.groups.find(x => x.id === g.parentId); if (!g) break; d++; } return d; };
        [...state.groups].sort((a, b) => { const path = x => { const names = [x.name]; let next = state.groups.find(y => y.id === x.parentId); let guard = 0; while (next && guard++ < 128) { names.unshift(next.name); next = state.groups.find(y => y.id === next.parentId); } return names.join("/"); }; return path(a).localeCompare(path(b)); }).forEach(group => {
            const row = node("div", undefined, "group-item"); const text = node("div"); text.style.paddingLeft = depth(group) * 20 + "px"; text.append(node("strong", "▤ " + group.name));
            text.append(node("p", group.parentId ? "Undergrupp" : "Rotgrupp")); row.append(text); const actions = node("div", undefined, "actions");
            if (group.canManage) actions.append(button("Behörigheter", () => accessDialog(0, group)), button("Byt namn", () => renameDialog(0, group)), button("Flytta", () => moveDialog(0, group)), button("Ta bort", () => deleteTarget(0, group), "danger"));
            row.append(actions); p.append(row);
        });
    }
    async function resourcePage() {
        const resource = state.resource;
        const actions = [button("← Alla resurser", async () => { state.resource = null; await render(); })];
        if (resource.canManage) actions.push(button("Behörigheter", () => accessDialog(1, resource)), button("Byt namn", () => renameDialog(1, resource)), button("Flytta", () => moveDialog(1, resource)));
        if (has(4)) actions.push(button("Papperskorg", recycleBin));
        if (resource.canManage) actions.push(button("Ta bort resurs", () => deleteTarget(1, resource), "danger"));
        heading(resource.name, "Din åtkomst: " + (rights(resource.permissions) || "Behörighetsadministration"), actions);
        if (!has(1)) { empty($("content"), "Du administrerar åtkomsten", "Tilldela metadata-, läs- eller ändringsrätt för att använda resursens innehåll."); return; }
        const [secrets, licenses] = await Promise.all([api(`/resources/${resource.id}/secrets`), api(`/resources/${resource.id}/licenses`)]);
        const passwords = panel($("content"), "Lösenord", has(4) ? [button("+ Lägg till lösenord", () => secretDialog(), "primary")] : []);
        if (!secrets.length) empty(passwords, "Inga lösenord sparade", "Lägg till ett konto som hör till den här resursen.");
        else table(passwords, ["Konto", "Version", "Åtgärder"], secrets.map(secret => {
            const actions = node("div", undefined, "actions");
            if (has(2)) actions.append(button("Visa", () => revealSecret(secret)), button("Kopiera", () => copySecret(secret)));
            if (has(4)) actions.append(button("Ändra", () => secretDialog(secret)), button("Ta bort", () => deleteSecret(secret), "danger"));
            actions.append(button("Historik", () => historyDialog(secret)));
            if (has(2) && has(16) && secret.ldapProfileId) actions.append(button("LDAP-test", () => ldapTest(secret)));
            return [secret.title, String(secret.currentVersion), actions];
        }));
        const licensePanel = panel($("content"), "Licenser", has(4) ? [button("+ Lägg till licens", () => licenseDialog(), "primary")] : []);
        licenseTable(licensePanel, licenses, true);
    }
    function createGroup() {
        let name, parent;
        const body = dialog("Ny resursgrupp", "Skapa grupp", async () => { await api("/groups", { name: name.value, parentId: parent.value || null }); await load(); });
        name = field(body, "Namn", "text", "", true); name.maxLength = 200;
        parent = select(body, "Överordnad grupp", [...(state.session.isAccessAdministrator ? [["", "Ingen – skapa rotgrupp"]] : []), ...state.groups.filter(x => x.canManage).map(x => [x.id, x.name])]);
        hint(body, "Behörigheter och ägare från den överordnade gruppen ärvs.");
    }
    function createResource() {
        let name, group;
        const body = dialog("Ny resurs", "Skapa resurs", async () => { const result = await api("/resources", { name: name.value, groupId: group.value }); await load(); state.resource = state.resources.find(x => x.id === result.id); await render(); });
        name = field(body, "Resursnamn", "text", "", true); name.maxLength = 200;
        group = select(body, "Resursgrupp", state.groups.filter(x => x.canManage).map(x => [x.id, x.name]));
    }
    function renameDialog(kind, target) {
        let name; const body = dialog("Byt namn", "Spara", async () => { await api(`/${kind === 0 ? "groups" : "resources"}/${target.id}/rename`, { name: name.value, revision: target.revision }); await load(); });
        name = field(body, "Namn", "text", target.name, true); name.maxLength = 200;
    }
    function deleteTarget(kind, target) {
        const body = dialog("Ta bort · " + target.name, "Ta bort", async () => { await api(`/targets/${kind}/${target.id}/delete`, { deleted: true, revision: target.revision }); if (kind === 1) state.resource = null; await load(); });
        body.append(node("p", "Endast tomma grupper och resurser kan tas bort. Resurser med bevarad historik måste behållas."));
    }
    async function recycleBin() {
        const [secrets, licenses] = await Promise.all([api(`/resources/${state.resource.id}/deleted-secrets`), api(`/resources/${state.resource.id}/deleted-licenses`)]);
        const body = dialog("Papperskorg · " + state.resource.name);
        table(body, ["Post", "Typ", ""], [...secrets.map(x => [x.title, "Lösenord", button("Återställ", async () => { await api(`/secrets/${x.id}/deleted`, { deleted: false, revision: x.revision }); closeDialog(); await render(); })]),
            ...licenses.map(x => [x.product, "Licens", button("Återställ", async () => { await api(`/licenses/${x.id}/deleted`, { deleted: false, revision: x.revision }); closeDialog(); await render(); })])]);
        if (!secrets.length && !licenses.length) hint(body, "Papperskorgen är tom.");
    }
    function secretDialog(secret) {
        safely(async () => {
            const payload = secret && has(2) ? await api(`/secrets/${secret.id}/reveal`, {}) : { username: "", password: "", notes: "" };
            let title, username, password, notes, profile;
            const body = dialog(secret ? "Ändra lösenord" : "Lägg till lösenord", "Spara", async () => {
                const data = { title: title.value, payload: { username: username.value, password: password.value, notes: notes.value }, ldapProfileId: profile.value || null, revision: secret?.revision || 0 };
                await api(secret ? `/secrets/${secret.id}/update` : `/resources/${state.resource.id}/secrets`, data); await render();
            });
            if (secret && !has(2)) hint(body, "Du saknar läsrätt. Alla hemliga fält ersätts med de värden du anger här.");
            title = field(body, "Benämning", "text", secret?.title || "", true); title.maxLength = 200;
            username = field(body, "Användarnamn", "text", payload.username); username.dataset.sensitive = "true"; username.maxLength = 512;
            password = field(body, "Lösenord", "password", payload.password, true); password.maxLength = 65536;
            body.append(button("Generera lösenord", () => { const bytes = crypto.getRandomValues(new Uint8Array(24)); password.value = Array.from(bytes, x => x.toString(16).padStart(2, "0")).join(""); }));
            notes = field(body, "Hemliga anteckningar", "textarea", payload.notes); notes.maxLength = 65536;
            profile = select(body, "LDAP-testprofil", [["", "Ingen"], ...state.profiles.map(x => [x.id, x.name])], secret?.ldapProfileId || "");
        });
    }
    async function revealSecret(secret, version) {
        const payload = await api(`/secrets/${secret.id}/reveal`, { version: version ?? null });
        const body = dialog(secret.title + (version ? ` · version ${version}` : ""));
        [ ["Användarnamn", payload.username], ["Lösenord", payload.password], ["Anteckningar", payload.notes] ].forEach(([label, value]) => { const input = field(body, label, "textarea", value); input.readOnly = true; input.classList.add("secret-value"); });
        hint(body, "Visningen är auditerad. Fälten töms automatiskt efter 30 sekunder eller när sidan tappar fokus.");
        secretTimer = setTimeout(closeDialog, 30000);
    }
    async function copySecret(secret) {
        const payload = await api(`/secrets/${secret.id}/reveal`, { copy: true });
        await navigator.clipboard.writeText(payload.password); notice("Lösenordet har kopierats. Töm urklipp när du är klar.");
    }
    function deleteSecret(secret) {
        const body = dialog("Ta bort lösenord", "Ta bort", async () => { await api(`/secrets/${secret.id}/deleted`, { deleted: true, revision: secret.revision }); await render(); });
        body.append(node("p", `Ta bort ${secret.title}? Historiska versioner bevaras krypterade.`));
    }
    async function historyDialog(secret) {
        const versions = await api(`/secrets/${secret.id}/versions`); const body = dialog("Versionshistorik · " + secret.title);
        table(body, ["Version", "Datum", "Ändrad av", "Åtgärder"], versions.map(version => {
            const actions = node("div", undefined, "actions");
            if (has(2)) actions.append(button("Visa", () => revealSecret(secret, version.version)));
            if (has(2) && has(4) && version.version !== secret.currentVersion) actions.append(button("Återställ", () => {
                const confirmation = dialog("Återställ tidigare version", "Återställ", async () => { await api(`/secrets/${secret.id}/restore`, { version: version.version, revision: secret.revision }); await render(); });
                confirmation.append(node("p", `Version ${version.version} blir en ny aktuell version. Återställningen auditeras.`));
            }));
            return [String(version.version), date(version.createdAt), version.createdBy, actions];
        }));
    }
    function ldapTest(secret) {
        const body = dialog("Testa sparat lösenord mot LDAP", "Genomför ett test", async () => {
            const result = await api(`/secrets/${secret.id}/ldap-test`, {});
            const messages = ["Autentiseringen lyckades.", "Autentiseringen nekades. Lösenord, kontostatus eller policy kan vara orsaken.", "Anslutningen kunde inte verifieras."];
            return messages[result.result] || result.result;
        });
        body.append(node("p", "Testet använder det aktuella sparade kontot och lösenordet. Ett misslyckat försök kan bidra till kontolåsning i AD."));
        hint(body, "Ett bind-försök utförs utan automatiska återförsök. Kontot kan testas högst en gång per fem minuter i denna tjänsteinstans.");
    }
    function directoryPicker(parent) {
        const kind = select(parent, "Typ", [["0", "Användare"], ["1", "AD-grupp"]]); const query = field(parent, "Sök i Active Directory", "text"); query.maxLength = 128;
        const results = select(parent, "Välj träff", [["", "Sök och välj en träff"]]); let subjects = [];
        parent.append(button("Sök", async () => { subjects = await api(`/directory?query=${encodeURIComponent(query.value)}&kind=${kind.value}`); results.replaceChildren(); subjects.forEach(subject => { const o = node("option", subject.name); o.value = subject.id; results.append(o); }); if (!subjects.length) { const o = node("option", "Inga träffar"); o.value = ""; results.append(o); } }));
        kind.addEventListener("change", () => { subjects = []; results.replaceChildren(); });
        return () => { const selected = subjects.find(x => x.id === results.value); if (!selected) throw new Error("Sök och välj en användare eller grupp först."); return selected; };
    }
    async function accessDialog(kind, target) {
        const access = await api(`/access/${kind}/${target.id}`); const body = dialog("Behörigheter · " + target.name);
        hint(body, "Tilldelningar summeras. Ärvd åtkomst ändras på den överordnade gruppen. Ägare och behörighetsadministratörer kan tilldela sig själva läsrätt.");
        const actions = node("div", undefined, "actions"); actions.append(button("+ Tilldela åtkomst", () => grantDialog(kind, target)), button("+ Lägg till ägare", () => ownerDialog(kind, target))); body.append(actions);
        table(body, ["Identitet", "Rättigheter", "Giltighet", ""], access.grants.map(grant => {
            const direct = grant.targetKind === kind && grant.targetId === target.id;
            return [grant.subjectId, rights(grant.permissions), grant.expiresAt ? `${date(grant.startsAt)} – ${date(grant.expiresAt)}` : "Tills vidare",
                direct ? button("Återkalla", async () => { await api(`/grants/${grant.id}/revoke`, {}); await accessDialog(kind, target); }) : node("span", "Ärvd", "badge")];
        }));
        body.append(node("h3", "Resursägare"));
        table(body, ["Identitet", ""], access.owners.map(owner => [owner.subjectId,
            owner.targetKind === kind && owner.targetId === target.id ? button("Ta bort ägare", async () => { await api(`/owners/${owner.id}/remove`, {}); await accessDialog(kind, target); }) : node("span", "Ärvd", "badge")]));
    }
    function grantDialog(kind, target) {
        let pick, temporary, start, expiry; const checks = [];
        const body = dialog("Tilldela åtkomst · " + target.name, "Tilldela", async () => {
            const subject = pick(); const temp = temporary.value === "temporary";
            const permissions = temp ? 3 : checks.filter(x => x.input.checked).reduce((value, x) => value | x.bit, 1);
            await api("/grants", { targetKind: kind, targetId: target.id, subjectKind: subject.kind, provider: subject.provider, subjectId: subject.id,
                permissions, startsAt: temp ? new Date(start.value).toISOString() : null, expiresAt: temp ? new Date(expiry.value).toISOString() : null }); await load();
        });
        pick = directoryPicker(body);
        temporary = select(body, "Giltighet", [["permanent", "Tills vidare"], ["temporary", "Tillfällig läsrätt – endast användare"]]);
        [[2, "Läsa hemligheter"], [4, "Ändra innehåll"], [8, "Hantera behörigheter"], [16, "Testa mot LDAP"]].forEach(([bit, label]) => { const row = node("div", undefined, "check-row"); const input = field(row, label, "checkbox"); input.value = "on"; checks.push({ bit, input }); body.append(row); });
        const times = node("div", undefined, "field-row"); const localDate = date => new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
        start = field(times, "Från", "datetime-local", localDate(new Date())); expiry = field(times, "Till", "datetime-local", localDate(new Date(Date.now() + 3600000))); body.append(times); times.hidden = true;
        temporary.addEventListener("change", () => { const temp = temporary.value === "temporary"; times.hidden = !temp; start.required = expiry.required = temp; checks.forEach(x => x.input.disabled = temp); });
        hint(body, "Metadataåtkomst ingår. Tillfällig rätt stoppar framtida utlämning men kan inte återkalla ett redan kopierat lösenord.");
    }
    function ownerDialog(kind, target) {
        let pick; const body = dialog("Lägg till ägare · " + target.name, "Lägg till ägare", async () => { const subject = pick(); await api("/owners", { targetKind: kind, targetId: target.id, subjectKind: subject.kind, provider: subject.provider, subjectId: subject.id }); await load(); });
        pick = directoryPicker(body); hint(body, "Ägaren får hantera åtkomst inom resursen eller gruppens underträd.");
    }
    function moveDialog(kind, target) {
        let destination; const body = dialog("Flytta · " + target.name);
        destination = select(body, "Ny resursgrupp", state.groups.filter(x => x.canManage && x.id !== target.id).map(x => [x.id, x.name]));
        body.append(button("Förhandsgranska åtkomst", async () => {
            const selected = destination.value; const preview = await api(`/move/${kind}/${target.id}/preview`, { destinationGroupId: selected, revision: target.revision });
            const summary = dialog("Bekräfta flytt · " + target.name, "Flytta", async () => { await api(`/move/${kind}/${target.id}`, { destinationGroupId: selected, revision: target.revision }); await load(); });
            hint(summary, "Följande tilldelningar blir tillämpliga efter flytten. För en grupp påverkas även dess underträd; direkta tilldelningar längre ned bevaras.");
            table(summary, ["Identitet", "Rättigheter", "Gäller till"], preview.grants.map(x => [x.subjectId, rights(x.permissions), date(x.expiresAt)]));
            table(summary, ["Ägare"], preview.owners.map(x => [x.subjectId]));
        }, "primary"));
    }
    function licenseTable(parent, licenses, scoped) {
        if (!licenses.length) { empty(parent, "Inga licenser ännu", "Registrera programlicenser, platser och giltighetstid."); return; }
        table(parent, ["Produkt", "Platser", "Giltig till", "Åtgärder"], licenses.map(license => {
            const resource = state.resources.find(x => x.id === license.resourceId); const actions = node("div", undefined, "actions");
            if (resource?.permissions & 2) actions.append(button("Visa nyckel", () => revealLicense(license)));
            if (resource?.permissions & 4) actions.append(button("Ändra", () => { state.resource = resource; licenseDialog(license); }));
            if (resource?.permissions & 4) actions.append(button("Ta bort", () => {
                const body = dialog("Ta bort licens · " + license.product, "Ta bort", async () => { await api(`/licenses/${license.id}/deleted`, { deleted: true, revision: license.revision }); await render(); });
                body.append(node("p", "Licensen flyttas till resursens papperskorg. Tilldelningar bevaras för återställning."));
            }, "danger"));
            actions.append(button("Tilldelningar", () => assignmentDialog(license, resource)));
            const product = node("div"); product.append(node("strong", license.product), node("p", scoped ? license.vendor : (resource?.name || "") + " · " + license.vendor, "hint"));
            const expiry = node("span", date(license.expiresAt), license.expiresAt && new Date(license.expiresAt) < new Date(Date.now() + 30 * 86400000) ? "badge warn" : "");
            return [product, `${license.assignedSeats} / ${license.seats}`, expiry, actions];
        }));
    }
    async function licenseOverview() {
        heading("Licensöversikt", "Tilldelade platser och kommande utgångsdatum för dina synliga resurser.");
        const licenses = (await Promise.all(state.resources.filter(x => x.permissions & 1).map(x => api(`/resources/${x.id}/licenses`)))).flat();
        const stats = node("div", undefined, "summary-grid"); const expiring = licenses.filter(x => x.expiresAt && new Date(x.expiresAt) < new Date(Date.now() + 30 * 86400000)).length;
        [[licenses.length, "Registrerade licenser"], [licenses.reduce((a, x) => a + x.assignedSeats, 0), "Tilldelade platser"], [expiring, "Utgångna eller förfaller inom 30 dagar"]].forEach(([value, label]) => { const stat = node("div", undefined, "summary-card"); stat.append(node("strong", String(value)), node("span", label)); stats.append(stat); }); $("content").append(stats);
        licenseTable(panel($("content"), "Programlicenser"), licenses, false);
    }
    function licenseDialog(license) {
        safely(async () => {
            const payload = license && has(2) ? await api(`/licenses/${license.id}/reveal`, {}) : { licenseKey: "", notes: "" };
            let product, vendor, reference, seats, expiry, key, notes;
            const body = dialog(license ? "Ändra licens" : "Lägg till licens", "Spara", async () => {
                await api(license ? `/licenses/${license.id}/update` : "/licenses", { resourceId: state.resource.id, product: product.value, vendor: vendor.value, purchaseReference: reference.value,
                    seats: Number(seats.value), expiresAt: expiry.value ? new Date(expiry.value + "T23:59:59").toISOString() : null,
                    payload: { licenseKey: key.value, notes: notes.value }, revision: license?.revision || 0 }); await render();
            });
            if (license && !has(2)) hint(body, "Du saknar läsrätt. Licensnyckel och anteckningar ersätts med värdena du anger.");
            product = field(body, "Produkt", "text", license?.product || "", true); vendor = field(body, "Leverantör", "text", license?.vendor || "");
            reference = field(body, "Inköpsreferens", "text", license?.purchaseReference || ""); seats = field(body, "Antal platser", "number", String(license?.seats || 1), true); seats.min = "1"; seats.max = "1000000";
            expiry = field(body, "Giltig till", "date", license?.expiresAt?.slice(0, 10) || "");
            key = field(body, "Licensnyckel", "password", payload.licenseKey); notes = field(body, "Hemliga anteckningar", "textarea", payload.notes);
        });
    }
    async function revealLicense(license) {
        const payload = await api(`/licenses/${license.id}/reveal`, {}); const body = dialog("Licensnyckel · " + license.product);
        const key = field(body, "Licensnyckel", "textarea", payload.licenseKey); key.readOnly = true;
        const notes = field(body, "Anteckningar", "textarea", payload.notes); notes.readOnly = true;
        hint(body, "Visningen är auditerad. Fälten töms efter 30 sekunder eller när sidan tappar fokus."); secretTimer = setTimeout(closeDialog, 30000);
    }
    async function assignmentDialog(license, resource) {
        const assignments = await api(`/licenses/${license.id}/assignments`); const body = dialog("Tilldelningar · " + license.product);
        hint(body, `${license.assignedSeats} av ${license.seats} platser tilldelade.`);
        if (resource?.permissions & 4) body.append(button("+ Tilldela platser", () => {
            let mode, pick, target, seats; const form = dialog("Tilldela licensplatser", "Tilldela", async () => {
                const subject = mode.value === "user" ? pick() : null;
                await api("/assignments", { licenseId: license.id, resourceId: mode.value === "resource" ? target.value : null,
                    userProvider: subject?.provider || null, userId: subject?.id || null, seats: Number(seats.value) }); await render();
            });
            mode = select(form, "Tilldela till", [["user", "Användare"], ["resource", "Resurs"]]);
            const users = node("div"); pick = directoryPicker(users); users.querySelector("select").disabled = true; form.append(users);
            const resources = node("div"); target = select(resources, "Resurs", state.resources.filter(x => x.permissions & 1).map(x => [x.id, x.name])); form.append(resources); resources.hidden = true;
            mode.addEventListener("change", () => { users.hidden = mode.value !== "user"; resources.hidden = mode.value !== "resource"; });
            seats = field(form, "Antal platser", "number", "1", true); seats.min = "1"; seats.max = String(license.seats);
        }));
        table(body, ["Tilldelad till", "Platser", "Datum", ""], assignments.map(x => [x.userId || state.resources.find(r => r.id === x.resourceId)?.name || x.resourceId, String(x.seats), date(x.createdAt),
            resource?.permissions & 4 ? button("Återta", async () => { await api(`/assignments/${x.id}/remove`, {}); await render(); closeDialog(); }) : ""]));
    }
    async function auditPage() {
        heading("Auditlogg", "Signerade händelser i separat databas. Tider visas i din lokala tidszon.");
        const toolbar = node("div", undefined, "toolbar"); const from = field(toolbar, "Från", "datetime-local"); const to = field(toolbar, "Till (exklusive)", "datetime-local");
        const actor = field(toolbar, "Aktörens ID", "text"); const action = field(toolbar, "Operation", "text"); $("content").append(toolbar);
        const panelNode = panel($("content"), "Händelser"); const results = node("div"); panelNode.append(results); let offset = 0;
        function query() { const params = new URLSearchParams({ offset }); if (from.value) params.set("from", new Date(from.value).toISOString()); if (to.value) params.set("to", new Date(to.value).toISOString()); if (actor.value) params.set("actorId", actor.value); if (action.value) params.set("action", action.value); return params.toString(); }
        const paint = async () => {
            const events = await api("/audit?" + query()); results.replaceChildren();
            if (!events.length) { empty(results, "Inga händelser", "Ändra filtret eller gå till föregående sida."); return; }
            table(results, ["Tid", "Aktör", "Operation", "Resultat", "Verifiering"], events.map(x => [date(x.event.timestamp), node("span", x.event.actorId, "audit-id"),
                x.event.action, x.event.outcome, node("span", x.signatureValid ? "Giltig signatur" : "Ogiltig signatur", "badge" + (x.signatureValid ? "" : " error"))]));
        };
        toolbar.append(button("Filtrera", async () => { offset = 0; await paint(); }), button("Exportera denna sida", async () => {
            const response = await fetch("/api/audit/export?" + query(), { credentials: "same-origin", cache: "no-store" }); if (!response.ok) throw new Error("Auditexporten misslyckades.");
            const url = URL.createObjectURL(await response.blob()); const link = node("a"); link.href = url; link.download = "valvra-audit.json"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        }));
        const paging = node("div", undefined, "actions"); paging.append(button("← Föregående", async () => { offset = Math.max(0, offset - 200); await paint(); }), button("Nästa →", async () => { offset += 200; await paint(); })); $("content").append(paging); await paint();
    }
    document.querySelectorAll("[data-view]").forEach(item => item.addEventListener("click", () => safely(async () => { closeDialog(); state.view = item.dataset.view; state.resource = null; await render(); })));
    $("close-dialog").addEventListener("click", closeDialog); $("dialog").addEventListener("cancel", closeDialog);
    $("refresh").addEventListener("click", () => safely(load)); window.addEventListener("blur", clearSecrets);
    document.addEventListener("visibilitychange", () => { if (document.hidden) clearSecrets(); });
    ["pointerdown", "keydown"].forEach(type => document.addEventListener(type, () => lastActivity = Date.now()));
    setInterval(() => { if (Date.now() - lastActivity > 300000) closeDialog(); }, 10000);
    safely(async () => { state.session = await api("/session"); $("identity").textContent = state.session.displayName; $("audit-nav").hidden = !state.session.isAuditor; await load(); });
})();
