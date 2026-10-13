"use strict";
(() => {
    // Configuration drafts contain directory metadata only, never connection secrets.
    let draft, query = "ad", busy = false, dirty = false, draftEpoch = 0;
    const roleLabels = [[32, "Behörighetsadministratör"], [64, "Systemadministratör"], [128, "Auditläsare"]];
    async function render(ui) {
        const {state, api, node, button, field, panel, table, hint, heading, dialog, notice, t} = ui;
        const content = document.getElementById("content");
        heading(t("Inställningar"), t("Hantera kataloganslutning och personbundna administratörsrättigheter."));
        const section = state.settingsSection;
        const sections = [["people","Personer och behörigheter",true], ["import","Import",state.session.isAccessAdministrator],
            ["directory","Katalog",state.session.isSystemAdministrator], ["certificates","Certifikat och säkerhet",true], ["about","Om Valvra",true]];
        const tabs = node("nav", undefined, "settings-navigation"); tabs.setAttribute("aria-label", t("Inställningssektioner"));
        sections.filter(([, , visible]) => visible).forEach(([key,label]) => { const link = ui.routeLink(t(label), {view:"settings",section:key}); if (key === section) link.setAttribute("aria-current","page"); tabs.append(link); }); content.append(tabs);
        if (dirty) hint(content, t("Kataloginställningar har osparade ändringar. Utkastet finns kvar i denna flik."));
        if (section === "import") {
            const p = panel(content,t("Import"));
            hint(p,t("Import kräver behörighetsadministration och destinationens metadata- och ändringsrätt. Befintliga lösenord skrivs aldrig över."));
            p.append(button(t("Importera lösenord"),ui.openImport,"primary")); return;
        }
        if (section === "about") {
            const p = panel(content,t("Om Valvra"));
            hint(p,"Valvra " + (state.session.applicationVersion || "—"));
            p.append(node("p", state.session.build || "—", "audit-id"));
            hint(p,t("Versionsinformationen kommer från den körande applikationen.")); return;
        }
        if (section === "certificates") {
            const rows = await api("/settings/certificates"); if (!ui.isCurrent()) return;
            const p = panel(content,t("Certifikat och säkerhet"));
            const purposes = {Encryption:"Kryptering",Audit:"Auditsignering",Integrity:"Integritet"};
            const statuses = {Unconfigured:"Inte konfigurerat",NotAssessed:"Ej bedömt på denna plattform",Missing:"Saknas",Ambiguous:"Tvetydigt",Expired:"Utgånget",NotYetValid:"Ännu inte giltigt",Expiring:"Förfaller inom 30 dagar",Present:"Finns",Unavailable:"Status kunde inte läsas"};
            table(p,[t("Användning"),t("Status"),t("Giltig till"),t("Privat nyckel")],rows.map(x=>{
                const identity=node("div"); identity.append(node("strong",t(purposes[x.purpose] || "Certifikat")),node("p",t(x.active ? "Aktivt" : "Behållet för äldre data"),"hint"),node("p",x.thumbprint || "—","audit-id"));
                return [identity,t(statuses[x.status] || "Status kunde inte läsas"),x.expiresAt ? new Date(x.expiresAt).toLocaleString(window.ValvraI18n.locale) : "—",x.hasPrivateKey == null ? "—" : t(x.hasPrivateKey ? "Finns; åtkomst ej testad" : "Saknas")];
            }));
            hint(p,t("Status läser endast certifikatmetadata. Privatnyckelåtkomst och återställning är inte testade. Äldre verifieringscertifikat kan behövas även efter utgångsdatum."));
            const guide=panel(content,t("Planera certifikatrotation")), steps=node("ol"); guide.append(steps);
            ["Säkerhetskopiera databaser, App_Data, certifikat och återställningsmaterial. Kontrollera att återställning fungerar.",
                "Installera nya certifikat och kontrollera tjänstekontots privatnyckelåtkomst. Behåll äldre krypterings-, audit- och integritetscertifikat och deras konfiguration.",
                "Planera ett underhållsfönster. Stoppa och verifiera webbplatsens applikationspool. Konfigurera nya aktiva tumavtryck och behåll äldre tillåtna/verifierande tumavtryck.",
                "Kör Valvra.Web.exe --rewrap-keys från publiceringskatalogen som ett aktivt AD-personkonto med behörighetsadministration och nödvändig databas- och certifikatåtkomst. Avbruten ompackning kan köras igen.",
                "Starta applikationspoolen och verifiera nya och äldre hemlighetsversioner, audit och integritet. Behåll äldre nycklar under hela säkerhetskopiornas återställningsperiod."
            ].forEach(text=>steps.append(node("li",t(text))));
            hint(guide,t("TLS-certifikatet hanteras separat i webbservern. Ingen nyckel ändras eller tas bort från denna sida.")); return;
        }
        if (section === "directory" && state.session.isSystemAdministrator) {
            const providers = await api("/settings/providers");
            const loaded = draft || await api("/settings/directory");
            if (!ui.isCurrent() || state.view !== "settings") return;
            if (!draft) draft = loaded;
            const directory = panel(content, t("Inloggning och katalog")); directory.classList.add("settings-panel");
            const installed = node("p");
            installed.textContent = providers.logins.find(x => x.id === draft.loginProviderId)?.name + " · "
                + providers.directories.find(x => x.id === draft.directoryProviderId)?.name;
            directory.append(installed);
            hint(directory, t("Installerade moduler. Byte av identitetsprovider kräver planerad koppling av befintliga identiteter."));
            const form = node("form", undefined, "settings-form"); directory.append(form);
            const inputs = {};
            function bind(parent, key, label, type = "text", required = false, help) {
                const input = field(parent, label, type, draft[key], required); inputs[key] = input;
                input.maxLength = key === "server" ? 253 : 2048;
                if (type === "number") { input.min = 1; input.max = key === "port" ? 65535 : 30; }
                input.addEventListener("input", () => { draftEpoch++; dirty = true; draft[key] = type === "number" ? Number(input.value) : input.value.trim(); });
                if (help) { const description = node("p", help, "hint"); description.id = input.id + "-help"; input.setAttribute("aria-describedby", description.id); parent.append(description); }
                return input;
            }
            bind(form, "server", t("Domänkontrollantens DNS-namn"), "text", true);
            bind(form, "lookupBaseDn", t("Katalogens uppslagsbas"), "text", true,
                t("Används för inloggning och gruppmedlemskap. Exempel: DC=example,DC=se."));
            bind(form, "userSearchBaseDn", t("Sökbas för personkonton (valfritt)"), "text", false,
                t("Exempel: OU=Users,DC=example,DC=se. Tomt fält använder uppslagsbasen."));
            bind(form, "groupSearchBaseDn", t("Sökbas för behörighetsgrupper (valfritt)"), "text", false,
                t("Begränsar vilka säkerhetsgrupper som kan väljas vid tilldelning. Tomt fält använder uppslagsbasen."));
            const advanced = node("details"); advanced.append(node("summary", t("Avancerad anslutning"))); form.append(advanced);
            bind(advanced, "port", t("LDAPS-port"), "number", true);
            bind(advanced, "timeoutSeconds", t("Timeout i sekunder"), "number", true);
            const search = field(form, t("Söktext för anslutningstest"), "text", query, true);
            search.minLength = 2; search.maxLength = 128; search.addEventListener("input", () => { query = search.value; });
            hint(form, t("Testet visar upp till fem personkonton och fem grupper. Spara kontrollerar anslutningen igen."));
            const result = node("div"); result.setAttribute("role", "status"); form.append(result);
            const error = node("p", undefined, "error"); error.setAttribute("role", "alert"); form.append(error);
            const actions = node("div", undefined, "actions"); form.append(actions);
            const test = button(t("Testa anslutning"), () => perform(false));
            const save = node("button", t("Spara inställningar"), "button primary"); save.type = "submit";
            const reload = button(t("Läs in sparade inställningar"), async () => { draftEpoch++; draft = null; dirty = false; await ui.render(); });
            actions.append(test, save, reload);
            [test, save, reload].forEach(x => x.disabled = busy);
            async function perform(saving) {
                if (busy || !form.reportValidity()) return;
                busy = true; [test, save, reload].forEach(x => x.disabled = true); error.textContent = "";
                const submittedEpoch = draftEpoch;
                try {
                    const response = await api(saving ? "/settings/directory" : "/settings/directory/test", {settings: {...draft}, query: query.trim()});
                    result.replaceChildren();
                    if (saving) { if (submittedEpoch === draftEpoch) { draft = response; dirty = false; } else { draft = {...draft,revision:response.revision}; dirty = true; } notice(t("Kataloginställningarna har sparats och gäller för nya förfrågningar.")); }
                    else {
                        result.append(node("p", t("Anslutningen och ditt personkonto har verifierats.")));
                        table(result, [t("Typ"), t("Namn"), t("Identitet")], [...response.users.map(x => [t("Personkonto"), x.name, x.id]), ...response.groups.map(x => [t("Behörighetsgrupp"), x.name, x.id])]);
                    }
                } catch (failure) { error.textContent = failure.message; }
                finally { busy = false; [test, save, reload].forEach(x => x.disabled = false); if (!ui.isCurrent() && state.view === "settings" && state.settingsSection === "directory") await ui.render(); }
            }
            form.addEventListener("submit", event => { event.preventDefault(); perform(true); });
            form.addEventListener("invalid", event => { event.target.setAttribute("aria-invalid", "true"); error.textContent = t("Kontrollera fältet ”{field}”. {reason}", {field: event.target.labels[0].textContent, reason: window.ValvraI18n.validation(event.target)}); }, true);
        }
        if (section !== "people") return;
        const roles = await api("/settings/roles");
        if (!ui.isCurrent() || state.view !== "settings") return;
        const administrators = panel(content, t("Globala rättigheter"), state.session.isAccessAdministrator ? [button(t("+ Tilldela personkonto"), () => editRole())] : []); administrators.classList.add("settings-panel");
        hint(administrators, t("Tilldelas endast personkonton. Ger ingen automatisk läsrätt till lösenord eller licensnycklar. Den sista administratören för en rättighet kan inte tas bort."));
        function personLabel(role) {
            const label = node("div");
            const name = role.subjectId === state.session.subjectId && role.provider === state.session.provider ? state.session.displayName : role.name;
            label.append(node("strong", name || role.subjectId));
            if (name && name !== role.subjectId) label.append(node("p", role.subjectId, "hint audit-id"));
            return label;
        }
        table(administrators, [t("Personkonto"), t("Rättigheter"), t("Åtgärder")], roles.map(role => [personLabel(role),
            roleLabels.filter(([bit]) => role.roles & bit).map(([,label]) => t(label)).join(", "),
            state.session.isAccessAdministrator ? button(t("Ändra"), () => editRole(role)) : "—"
        ]));
        hint(administrators, t("Behörighetsadministratören tilldelar åtkomst och globala rättigheter. Systemadministratören hanterar kataloginställningar. Auditläsaren granskar auditloggen."));
        async function editRole(existing) {
            let subject = existing ? {provider: existing.provider, id: existing.subjectId} : null;
            const checks = [];
            const body = dialog(existing ? t("Ändra globala rättigheter") : t("Tilldela globala rättigheter"), t("Spara"), async () => {
                if (!subject) throw new Error(t("Välj ett personkonto från katalogsökningen."));
                const bits = checks.reduce((value, [bit, input]) => value | (input.checked ? bit : 0), 0);
                await api("/settings/roles", {provider: subject.provider, subjectId: subject.id, kind: 0, roles: bits, revision: existing?.revision || 0});
                await ui.refreshSession(); await ui.render();
            });
            if (existing) body.append(personLabel(existing));
            else {
                const search = field(body, t("Sök personkonto"), "text"); search.minLength = 2; search.maxLength = 128;
                const results = node("div"), status = node("p"); status.setAttribute("role", "status");
                let searchEpoch = 0;
                const personChoiceLabel = person => person.name + (person.samAccountName ? " · " + person.samAccountName : person.name !== person.id ? " · " + person.id : "");
                const runSearch = async () => {
                    const epoch = ++searchEpoch;
                    subject = null; results.replaceChildren(); status.textContent = "";
                    if (search.value.trim().length < 2) throw new Error(t("Ange 2–128 tecken för katalogsökning."));
                    const people = await api("/settings/people?query=" + encodeURIComponent(search.value.trim()));
                    if (epoch !== searchEpoch || !results.isConnected) return;
                    results.replaceChildren();
                    people.forEach(person => results.append(button(personChoiceLabel(person), () => {
                        subject = person; status.textContent = personChoiceLabel(person) + " · " + person.provider + " · " + person.id;
                        results.replaceChildren();
                        checks[0][1].focus();
                    })));
                    if (!people.length) status.textContent = t("Inga träffar");
                };
                const searchButton = button(t("Sök"), runSearch, "", false);
                search.addEventListener("input", () => { searchEpoch++; subject = null; results.replaceChildren(); status.textContent = ""; });
                search.addEventListener("keydown", event => { if (event.key === "Enter" && !event.isComposing) { event.preventDefault(); event.stopPropagation(); searchButton.click(); } });
                body.append(searchButton, results, status);
            }
            const rights = node("fieldset"); rights.append(node("legend", t("Rättigheter"))); body.append(rights);
            roleLabels.forEach(([bit, label]) => { const row = node("div", undefined, "check-row"), input = field(row, t(label), "checkbox"); input.checked = !!(existing?.roles & bit); checks.push([bit, input]); rights.append(row); });
            hint(body, t("Avmarkera alla rättigheter för att ta bort tilldelningen. Ge bara de rättigheter personen behöver."));
        }
    }
    window.ValvraSettings = Object.freeze({render});
})();
