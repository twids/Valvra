"use strict";
(() => {
    const MAX_BYTES = 2 * 1024 * 1024, MAX_ROWS = 1000;
    const fail = key => { throw new Error(key); };
    const key = value => value.trim().normalize("NFC").toLocaleLowerCase("en-US");
    function parseCsv(text, delimiter = "auto") {
        if (new TextEncoder().encode(text).length > MAX_BYTES) fail("Filen får vara högst 2 MiB.");
        text = text.replace(/^\uFEFF/, "");
        function read(separator) {
            const records = []; let row = [], value = "", quoted = false, closed = false;
            const cell = () => { row.push(value); value = ""; closed = false; if (row.length > 64) fail("Filen har för många kolumner."); };
            const record = () => { cell(); if (row.some(x => x.length)) records.push(row); row = []; if (records.length > MAX_ROWS + 1) fail("Importen får innehålla högst 1000 poster."); };
            for (let i = 0; i < text.length; i++) {
                const ch = text[i];
                if (ch === "\0") fail("Ogiltig CSV-fil.");
                if (quoted) {
                    if (ch === '"' && text[i + 1] === '"') { value += '"'; i++; }
                    else if (ch === '"') { quoted = false; closed = true; }
                    else value += ch;
                } else if (ch === separator) cell();
                else if (ch === "\r" || ch === "\n") { if (ch === "\r" && text[i + 1] === "\n") i++; record(); }
                else if (ch === '"' && !value && !closed) quoted = true;
                else { if (closed || ch === '"') fail("Ogiltig CSV-fil."); value += ch; }
                if (value.length > 65536) fail("Ett fält överskrider tillåten längd.");
            }
            if (quoted) fail("Ogiltig CSV-fil.");
            if (value.length || row.length || closed) record();
            return records;
        }
        let records;
        if (delimiter === "auto") {
            const candidates = [",", ";", "\t"].map(separator => {
                try { const rows = read(separator); return {rows, separator}; } catch { return null; }
            }).filter(x => x && x.rows.length >= 2 && x.rows.every(row => row.length === x.rows[0].length));
            candidates.sort((a, b) => b.rows[0].length - a.rows[0].length);
            if (!candidates.length || candidates[0].rows[0].length < 2) fail("Ogiltig CSV-fil.");
            records = candidates[0].rows; delimiter = candidates[0].separator;
        } else { if (![",", ";", "\t"].includes(delimiter)) fail("Ogiltig CSV-fil."); records = read(delimiter); }
        if (records.length < 2) fail("Filen saknar poster.");
        const headers = records.shift().map(x => x.trim());
        if (headers.some(x => !x) || new Set(headers.map(key)).size !== headers.length || records.some(row => row.length !== headers.length)) fail("Kolumnrubriker eller antal kolumner är ogiltiga.");
        return {headers, rows: records, delimiter};
    }
    const aliases = {
        title: ["title", "titel", "name", "namn"], username: ["username", "user", "användarnamn", "login_username"],
        password: ["password", "lösenord", "login_password"], notes: ["notes", "anteckningar", "comment", "comments"],
        url: ["url", "website", "login_uri"], path: ["group", "grupp", "groups", "folder", "group path", "gruppsökväg", "group 1", "grupp 1", "group1", "grupp1"],
        subgroup: ["subgroup", "undergrupp", "group 2", "grupp 2", "group2", "grupp2"], resource: ["resource", "resurs"]
    };
    function suggest(headers) {
        return Object.fromEntries(Object.entries(aliases).map(([name, names]) => [name, headers.findIndex(header => names.includes(key(header)))]));
    }
    function mapRows(csv, mapping, defaultResource) {
        const assigned = Object.values(mapping).filter(x => x >= 0);
        if (new Set(assigned).size !== assigned.length || mapping.title < 0 || mapping.password < 0) fail("Mappa titel och lösenord till olika kolumner.");
        const get = (row, field) => mapping[field] >= 0 ? row[mapping[field]] : "";
        return csv.rows.map((row, index) => {
            const title = get(row, "title").trim(), resource = get(row, "resource").trim() || defaultResource.trim();
            const path = get(row, "path").trim().split(/[\\/]/).map(x => x.trim()).filter(Boolean);
            const subgroup = get(row, "subgroup").trim(); if (subgroup) path.push(subgroup);
            if (path.length > 2 || /[\\/]/.test(subgroup)) fail("Gruppsökvägen får ha högst två nivåer.");
            if (!title || title.length > 200 || !resource || resource.length > 200 || path.some(x => x.length > 200 || x === "." || x === "..")) fail("Titel, resurs och gruppnamn måste vara 1–200 tecken.");
            const username = get(row, "username"), password = get(row, "password"), notes = get(row, "notes"), url = get(row, "url");
            const combinedNotes = notes + (url ? (notes ? "\n\n" : "") + "URL: " + url : "");
            if (username.length > 512 || password.length > 65536 || combinedNotes.length > 65536) fail("Ett fält överskrider tillåten längd.");
            return {row: index + 2, title, resource, path, payload: {username, password, notes: combinedNotes}};
        });
    }
    // Names are matched only among visible siblings. Ambiguous names require manual mapping.
    function unique(items, name) {
        const matches = items.filter(x => key(x.name) === key(name));
        if (matches.length > 1) fail("Flera objekt har samma namn. Välj en befintlig destination.");
        return matches[0];
    }
    function plan(rows, groups, resources, baseId, targetId, overrides = {}) {
        const plannedGroups = [], plannedResources = [], entries = [], groupMap = new Map(), resourceMap = new Map();
        const allGroups = [...groups], allResources = [...resources];
        for (const row of rows) {
            let resource;
            if (targetId) {
                resource = resources.find(x => x.id === targetId);
                if (!resource || (resource.permissions & 5) !== 5) fail("Destinationen kräver metadata- och ändringsrätt.");
            } else {
                const pathKey = JSON.stringify(row.path);
                let parent = groupMap.get(pathKey);
                if (!parent) {
                    parent = groups.find(x => x.id === (overrides[pathKey] || baseId));
                    if (!parent) fail("Välj en destinationsgrupp.");
                    if (!overrides[pathKey]) for (const name of row.path) {
                        let child = unique(allGroups.filter(x => x.parentId === parent.id), name);
                        if (!child) {
                            if (!parent.canManage || (parent.permissions & 5) !== 5) fail("Nya grupper och resurser kräver administrations-, metadata- och ändringsrätt.");
                            child = {id: "group:" + plannedGroups.length, parentId: parent.id, name, canManage: true, permissions: parent.permissions};
                            allGroups.push(child); plannedGroups.push(child);
                        }
                        parent = child;
                    }
                    groupMap.set(pathKey, parent);
                }
                const resourceKey = JSON.stringify([parent.id, key(row.resource)]);
                resource = resourceMap.get(resourceKey) || unique(allResources.filter(x => x.groupId === parent.id), row.resource);
                if (!resource) {
                    if (!parent.canManage || (parent.permissions & 5) !== 5) fail("Nya grupper och resurser kräver administrations-, metadata- och ändringsrätt.");
                    resource = {id: "resource:" + plannedResources.length, groupId: parent.id, name: row.resource, permissions: parent.permissions};
                    allResources.push(resource); plannedResources.push(resource);
                }
                if ((resource.permissions & 5) !== 5) fail("Destinationen kräver metadata- och ändringsrätt.");
                resourceMap.set(resourceKey, resource);
            }
            entries.push({...row, resourceId: resource.id});
        }
        return {groups: plannedGroups, resources: plannedResources, entries};
    }
    async function execute(preview, api, cancelled, progress) {
        const ids = new Map(), report = {created: 0, skipped: 0, groups: 0, resources: 0, stopped: false, uncertainRow: null, errorCode: null, failedRow: null};
        const resolve = id => ids.get(id) || id;
        const stop = () => { if (cancelled()) { report.stopped = true; return true; } return false; };
        try {
            const operations = [
                ...preview.groups.map(x => ({key:x.id, kind:"group", target:x.parentId, name:x.name})),
                ...preview.resources.map(x => ({key:x.id, kind:"resource", target:x.groupId, name:x.name})),
                ...preview.entries.map(x => ({key:"row:" + x.row, kind:"secret", target:x.resourceId, name:x.title, payload:x.payload, row:x.row}))
            ];
            for (let offset = 0; offset < operations.length;) {
                if (stop()) return report;
                const batch = operations.slice(offset, offset + 12).map(x => ({...x, target:resolve(x.target)}));
                while (batch.length > 1 && new TextEncoder().encode(JSON.stringify({operations:batch})).length > 1536 * 1024) batch.pop();
                let response;
                while (!response) {
                    try { response = await api("/imports/batch", {operations:batch}); }
                    catch (error) {
                        // 429 is rejected before processing. Never retry an uncertain write or other failure.
                        if (error.status !== 429) throw error;
                        const until = Date.now() + (error.retryAfter || 60) * 1000;
                        progress({...report, waiting:true});
                        while (Date.now() < until) { if (stop()) return report; await new Promise(resolve => setTimeout(resolve, 250)); }
                        if (stop()) return report;
                    }
                }
                for (const result of response.results) {
                    const operation = batch.find(x => x.key === result.key);
                    if (!operation) throw new Error("Invalid import response");
                    ids.set(result.key, result.id);
                    if (operation.kind === "secret") {
                        if (result.created) report.created++; else report.skipped++;
                        operation.payload.password = operation.payload.notes = operation.payload.username = "";
                    } else if (result.created) report[operation.kind === "group" ? "groups" : "resources"]++;
                }
                progress({...report});
                if (response.errorCode) {
                    report.stopped = true; report.errorCode = response.errorCode;
                    report.failedRow = batch.find(x => x.key === response.failedKey)?.row || null;
                    report.uncertainRow = response.uncertainRow; return report;
                }
                offset += batch.length;
            }
        } catch { report.stopped = true; report.errorCode = "Transport"; report.uncertainRow = -1; }
        return report;
    }
    let clearActive = () => {}, transitioning = false, activeSession = 0;
    function clear(reason = "close") { if (!transitioning) { clearActive(); if (reason !== "blur") { clearActive = () => {}; activeSession++; } } }
    function open(ctx, safeDraft = {}) {
        const {state, api, node, button, field, select, hint, table, dialog, notice, load, t} = ctx;
        if (!state.session.isAccessAdministrator) { notice(t("Import kräver behörighetsadministration."), true); return; }
        if(!state.resources.some(x=>(x.permissions&5)===5)&&!state.groups.some(x=>x.canManage&&(x.permissions&5)===5)){
            const body=dialog(t("Importera lösenord"));hint(body,t("Välj först en destination där du har metadata- och ändringsrätt. Skapa en resurs eller be en resursägare tilldela åtkomst innan du väljer fil."));
            const link=node("a",t("Resurser"),"button");link.href="/resources";body.append(link);return;
        }
        const session = ++activeSession;
        let csv, rows = [], preview, stopped = false, busy = false, step = 0, mappingDraft={...safeDraft};
        const wipe = () => {
            stopped = true;
            csv?.rows.forEach(row => row.fill("")); csv = null;
            rows.forEach(row => { row.payload.username = row.payload.password = row.payload.notes = ""; }); rows = [];
            preview?.entries.forEach(row => { row.payload.username = row.payload.password = row.payload.notes = ""; });
            // Retain only destination IDs and user-entered configuration after clearing a file.
            delete mappingDraft.columns;delete mappingDraft.overrides;
            if (session === activeSession && document.getElementById("dialog").open && step) {
                document.getElementById("dialog-content").dataset.importCleared = "true";
                document.getElementById("dialog-error").textContent = t(busy ? "Importdata har tömts. Pågående batch kan slutföras. Kontrollera destinationen före ett nytt försök." : "Importdata har tömts. Välj filen igen.");
                document.querySelectorAll("#dialog-actions button[type=submit]").forEach(x => x.disabled = true);
                if(!busy&&!document.getElementById("import-reselect")){const reselect=button(t("Välj filen igen"),()=>open(ctx,mappingDraft));reselect.id="import-reselect";document.getElementById("dialog-content").append(reselect);}
            }
        };
        function show(title, label, submit) {
            transitioning = true;
            try { const body = dialog(t(title), label ? t(label) : null, submit); hint(body, t("Steg {step} av 4", {step:title === "Import · Resultat" ? 4 : step + 1})); return body; }
            finally { transitioning = false; if (session === activeSession) clearActive = wipe; }
        }
        const translateError = error => new Error(String(t(error.message)));
        const groupLabel = group => {
            const names = [group.name], seen = new Set([group.id]); let parent = group.parentId;
            while (parent && !seen.has(parent)) { seen.add(parent); const found = state.groups.find(x => x.id === parent); if (!found) break; names.unshift(found.name); parent = found.parentId; }
            return names.join(" / ");
        };
        function mappingStep() {
            step = 1; let mapping = {}, base, target, fallback, mode, overrides = {...mappingDraft.overrides};
            const body = show("Import · Mappa kolumner", "Förhandsgranska", async () => {
                if (stopped || !csv) throw new Error(String(t("Importdata har tömts. Välj filen igen.")));
                remember();
                try {
                    const selected = Object.fromEntries(Object.entries(mapping).map(([name, input]) => [name, Number(input.value)]));
                    if (mode.value === "resource") selected.path = selected.subgroup = selected.resource = -1;
                    rows = mapRows(csv, selected, mode.value === "resource" ? "Import" : fallback.value);
                    // Refresh destinations before planning; never trust the file for permissions.
                    [state.groups, state.resources] = await Promise.all([api("/groups"), api("/resources")]);
                    if (stopped || session !== activeSession) return false;
                    preview = plan(rows, state.groups, state.resources, base.value, mode.value === "resource" ? target.value : null, overrides);
                    const knownTitles = new Map();
                    for (const id of new Set(preview.entries.map(x => x.resourceId))) {
                        const current = id.startsWith("resource:") ? [] : await api(`/resources/${id}/secrets`);
                        if (stopped || session !== activeSession) return false;
                        knownTitles.set(id, new Set(current.map(x => key(x.title))));
                    }
                    for (const entry of preview.entries) {
                        const known = knownTitles.get(entry.resourceId), title = key(entry.title);
                        entry.skip = known.has(title); known.add(title);
                    }
                    previewStep();
                } catch (error) { throw translateError(error); }
                return false;
            });
            hint(body, t("Filen läses endast i denna fliks minne. Hemligheter visas inte i förhandsgranskningen."));
            hint(body, t("Gruppsökväg: IT/Servrar eller separata kolumner för grupp och undergrupp. Högst två nivåer."));
            const suggested = suggest(csv.headers), columns = [["-1", t("Ingen kolumn")], ...csv.headers.map((header, index) => [String(index), header])];
            const labels = {title: "Titel", username: "Användarnamn", password: "Lösenord", notes: "Anteckningar", url: "URL", path: "Gruppsökväg", subgroup: "Undergrupp", resource: "Resurskolumn"};
            for (const [name, label] of Object.entries(labels)) mapping[name] = select(body, t(label), columns, mappingDraft.columns?.[name]??String(suggested[name]));
            mode = select(body, t("Destination"), [["tree", t("Mappa gruppstruktur")], ["resource", t("En befintlig resurs")]], mappingDraft.mode || (state.resource || !state.groups.some(x => x.canManage && (x.permissions & 5) === 5) ? "resource" : "tree"));
            const tree = node("div"), single = node("div"); body.append(tree, single);
            base = select(tree, t("Basgrupp"), [["", t("Välj grupp")], ...state.groups.map(x => [x.id, groupLabel(x)])], mappingDraft.base??state.group?.id);
            fallback = field(tree, t("Resursnamn när kolumn saknas"), "text", mappingDraft.fallback??"Import", true); fallback.maxLength = 200;
            target = select(single, t("Resurs"), [["", t("Välj resurs")], ...state.resources.filter(x => (x.permissions & 5) === 5).map(x => [x.id, groupLabel(state.groups.find(g => g.id === x.groupId) || {name: ""}) + " / " + x.name])], mappingDraft.target??state.resource?.id);
            const paths = node("div"); tree.append(paths);
            function remember(){mappingDraft={columns:Object.fromEntries(Object.entries(mapping).map(([name,input])=>[name,input.value])),mode:mode.value,base:base.value,target:target.value,fallback:fallback.value,overrides:{...overrides}};}
            const update = () => {
                tree.hidden = mode.value !== "tree"; single.hidden = mode.value !== "resource"; fallback.required = !tree.hidden;
                paths.replaceChildren(); const previous=overrides;overrides = {};
                if (tree.hidden) {remember();return;}
                try {
                    const selected = Object.fromEntries(Object.entries(mapping).map(([name, input]) => [name, Number(input.value)]));
                    const mapped = mapRows(csv, selected, fallback.value || "Import");
                    for (const path of new Map(mapped.map(row => [JSON.stringify(row.path), row.path])).values()) {
                        const pathKey = JSON.stringify(path);
                        const picker = select(paths, path.join(" / ") || t("Utan grupp i filen"), [["", t("Återskapa under basgruppen")], ...state.groups.map(x => [x.id, groupLabel(x)])],previous[pathKey]||"");
                        overrides[pathKey]=picker.value;
                        picker.addEventListener("change", () => { overrides[pathKey] = picker.value;remember(); });
                    }
                } catch { overrides=previous; /* Keep choices until a valid mapping establishes which paths remain. */ }
                remember();
            };
            Object.values(mapping).forEach(input => input.addEventListener("change", update)); mode.addEventListener("change", update);base.addEventListener("change",remember);target.addEventListener("change",remember);fallback.addEventListener("input",update);update();
            hint(body, t("Nya objekt ärver destinationens behörigheter. Importen tilldelar aldrig nya rättigheter."));
        }
        function previewStep() {
            step = 2;
            let back;
            const body = show("Import · Förhandsgranskning", "Starta import", async () => {
                if (busy) return false;
                if (stopped) throw new Error(String(t("Importdata har tömts. Välj filen igen.")));
                busy = true;back.disabled=true;
                const status = node("p", t("Import pågår…")); status.setAttribute("role", "status"); body.append(status);
                body.append(button(t("Avbryt import"), () => { stopped = true; }));
                const report = await execute(preview, api, () => stopped || session !== activeSession, current => { if (session === activeSession) status.textContent = current.waiting ? t("Importen väntar på tjänstens anropsgräns. Avbryt stoppar nästa batch.") : t("Sparade: {created}. Överhoppade: {skipped}.", current); });
                busy = false; wipe(); if (session !== activeSession) return false; clearActive = () => {};
                const message = t("Sparade: {created}. Överhoppade: {skipped}. Nya grupper: {groups}. Nya resurser: {resources}.", report);
                const reasons = {Denied:"Importen nekades. Kontrollera administratörsrollen och destinationens rättigheter.", Invalid:"Importen stoppades av en ogiltig post eller tvetydig destination.", Conflict:"Destinationen har ändrats. Kontrollera den innan du försöker igen.", Unavailable:"Valvet eller auditanslutningen är inte tillgänglig. Kontrollera destinationen före ett nytt försök.", Transport:"Svaret uteblev. Den senaste batchen kan ha sparats. Kontrollera destinationen före ett nytt försök."};
                const reason = report.errorCode ? t(reasons[report.errorCode] || reasons.Transport) : "";
                if (report.stopped) {
                    notice(String(message) + " " + (reason || t("Importen stoppades. Redan sparade objekt finns kvar. Kontrollera destinationen före ett nytt försök.")), true);
                    if (report.uncertainRow > 0) notice(String(message) + " " + reason + " " + t("Rad {row} kan ha sparats trots att svaret uteblev. Kontrollera destinationen före ett nytt försök.", {row: report.uncertainRow}), true);
                } else notice(message);
                // Preserve the report while refreshing the ordinary resource list.
                await load();
                if (session !== activeSession) return false;
                if (document.getElementById("dialog").open && document.getElementById("dialog-title").textContent === String(t("Import · Förhandsgranskning"))) {
                    const result = show("Import · Resultat"); hint(result, message);
                    if (reason) hint(result, reason);
                    if (report.failedRow) hint(result, t("Kontrollera rad {row} i källfilen.", {row:report.failedRow}));
                    if (report.stopped) hint(result, t("Importen stoppades. Redan sparade objekt finns kvar. Kontrollera destinationen före ett nytt försök."));
                    if (report.uncertainRow > 0) hint(result, t("Rad {row} kan ha sparats trots att svaret uteblev. Kontrollera destinationen före ett nytt försök.", {row: report.uncertainRow}));
                }
                return false;
            });
            // Ordinary Back keeps the in-memory file; closing/blur still invokes wipe.
            back=button(t("← Tillbaka"),()=>{if(busy||stopped||!csv)throw new Error(String(t("Importdata har tömts. Välj filen igen.")));mappingStep();});body.append(back);
            hint(body, t("{count} poster. {groups} nya grupper och {resources} nya resurser.", {count: preview.entries.length, groups: preview.groups.length, resources: preview.resources.length}));
            hint(body, t("Befintliga titlar i samma resurs hoppas över. Inga lösenord skrivs över. Varje ändring auditeras separat."));
            hint(body, t("Avbrott stoppar efter pågående anrop. Redan sparade objekt tas inte bort."));
            const groupNames = new Map([...state.groups, ...preview.groups].map(x => [x.id, x]));
            const resourceNames = new Map([...state.resources, ...preview.resources].map(x => [x.id, x]));
            const label = entry => {
                const resource = resourceNames.get(entry.resourceId), names = [resource.name]; let id = resource.groupId; const seen = new Set();
                while (id && !seen.has(id)) { seen.add(id); const group = groupNames.get(id); if (!group) break; names.unshift(group.name); id = group.parentId; }
                return names.join(" / ");
            };
            table(body, [t("Rad"), t("Titel"), t("Destination"), t("Åtgärder")], preview.entries.slice(0, 100).map(x => [String(x.row), x.title, label(x), x.skip ? t("Hoppa över dubblett") : t("Skapa lösenord")]));
            if (preview.entries.length > 100) hint(body, t("De första 100 posterna visas. Alla validerade poster ingår i importen."));
        }
        const body = show("Importera lösenord", "Läs fil", async () => {
            const selected = file.files[0]; if (!selected) throw new Error(String(t("Välj en CSV-fil.")));
            if (selected.size > MAX_BYTES) throw new Error(String(t("Filen får vara högst 2 MiB.")));
            stopped = false; const epoch = ++readEpoch;
            try {
                const text = new TextDecoder("utf-8", {fatal: true}).decode(await selected.arrayBuffer());
                if (stopped || session !== activeSession || epoch !== readEpoch || !document.getElementById("dialog").open) return false;
                csv = parseCsv(text, separator.value); file.value = ""; mappingStep();
            } catch (error) { throw translateError(error instanceof TypeError ? new Error("Filen måste vara UTF-8-kodad.") : error); }
            return false;
        });
        hint(body,t("Destinationen måste tillåta metadata och ändringar. Kontrollera mappningen i förhandsgranskningen innan något sparas."));
        const sample=node("details");sample.append(node("summary",t("Visa ett ofarligt CSV-exempel")),node("pre","title,password,username\nExample account,EXAMPLE-ONLY,example-user"));body.append(sample);
        let readEpoch = 0;
        const file = field(body, t("CSV-fil"), "file", "", true); file.accept = ".csv,.tsv,text/csv,text/tab-separated-values";
        const separator = select(body, t("Avgränsare"), [["auto", t("Identifiera automatiskt")], [",", t("Komma")], [";", t("Semikolon")], ["\t", t("Tabb")]]);
        hint(body, t("UTF-8, högst 2 MiB och 1000 poster. CSV kan innehålla lösenord i klartext; hantera källfilen säkert."));
        hint(body, t("Obligatoriska kolumner: title och password. Valfria: username, notes, url, group, subgroup, resource. Kolumnerna kan mappas i nästa steg."));
    }
    const exports = {parseCsv, suggest, mapRows, plan, execute, open, clear};
    if (typeof module !== "undefined") module.exports = exports;
    else window.ValvraImport = exports;
})();
