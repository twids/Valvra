"use strict";
(() => {
    const $ = id => document.getElementById(id);
    const fields = {};
    let csrf = "", step = 0, passed = false;
    const sections = [];
    const node = (tag, text, className) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (className) n.className = className; return n; };
    function field(parent, key, label, value = "", type = "text", help = "") {
        const l = node("label", label); l.htmlFor = "setup-" + key;
        const input = node("input"); input.type = type; input.id = l.htmlFor; input.value = value; input.autocomplete = "off";
        fields[key] = input; parent.append(l, input); if (help) parent.append(node("p", help, "hint")); return input;
    }
    function select(parent, key, label, options, value) {
        const l = node("label", label); l.htmlFor = "setup-" + key; const input = node("select"); input.id = l.htmlFor;
        options.forEach(([v, text]) => { const o = node("option", text); o.value = v; input.append(o); }); if (value) input.value = value;
        fields[key] = input; parent.append(l, input); return input;
    }
    function section(title, text) { const container = node("section", undefined, "setup-fields-section"); container.append(node("h2", title), node("p", text, "hint")); sections.push(container); $("setup-fields").append(container); return container; }
    function connection(parent, prefix, title, database, authentication = "Windows") {
        const card = node("div", undefined, "connection-panel"); card.append(node("h3", title));
        select(card, prefix + "Provider", "Databastyp", [["SqlServer", "Microsoft SQL Server"], ["PostgreSql", "PostgreSQL"]], "SqlServer");
        field(card, prefix + "Server", "Databasserver", "", "text", "Använd serverns fullständiga DNS-namn. Ett betrott TLS-certifikat krävs.");
        field(card, prefix + "Database", "Databasnamn", database);
        field(card, prefix + "Port", "PostgreSQL-port", "5432", "number");
        select(card, prefix + "Authentication", "Autentisering", [["Windows", "Windows / Integrated Security"], ["Password", "Databaskonto och lösenord"]], authentication);
        field(card, prefix + "Username", "Databasanvändare (vid lösenord eller PostgreSQL-roll)");
        field(card, prefix + "Password", "Databaslösenord", "", "password");
        const update = () => { fields[prefix + "Password"].disabled = fields[prefix + "Authentication"].value === "Windows"; fields[prefix + "Port"].disabled = fields[prefix + "Provider"].value !== "PostgreSql"; };
        fields[prefix + "Authentication"].addEventListener("change", update); fields[prefix + "Provider"].addEventListener("change", update); update(); parent.append(card);
    }
    function value(key) { return fields[key].value.trim(); }
    function database(prefix) { return { provider: value(prefix + "Provider"), server: value(prefix + "Server"), database: value(prefix + "Database"), port: Number(value(prefix + "Port")),
        authentication: value(prefix + "Authentication"), username: value(prefix + "Username"), password: fields[prefix + "Password"].value }; }
    function request() {
        const vault = database("vault"), writer = database("writer"), reader = database("reader");
        const provision = fields.provision.checked;
        const cloneProvision = (db, prefix) => ({ ...db, authentication: value(prefix + "Authentication"), username: value(prefix + "Username"), password: fields[prefix + "Password"].value });
        return { allowedHosts: value("hosts"), vault, auditWriter: writer, auditReader: reader,
            auditReaderWindowsCredentials: reader.authentication === "Windows" && value("readerWindowsUser") ? { domain: value("readerWindowsDomain"), username: value("readerWindowsUser"), password: fields.readerWindowsPassword.value } : null,
            keyProtection: { activeThumbprint: value("encryptionThumbprint"), allowedThumbprints: value("oldEncryptionThumbprints").split(/[;,\s]+/).filter(Boolean) }, auditSigningThumbprint: value("auditThumbprint"),
            auditVerificationThumbprints: value("oldAuditThumbprints").split(/[;,\s]+/).filter(Boolean),
            activeDirectory: { server: value("adServer"), port: 636, baseDn: value("baseDn"), accessAdministratorGroupSid: value("adminSid"), auditorGroupSid: value("auditorSid"), timeoutSeconds: 10 },
            ldapTests: { profiles: value("testServer") ? [{ id: "default", name: value("testName") || "AD", server: value("testServer"), port: 636, domain: value("testDomain"), timeoutSeconds: 5 }] : [] },
            provisionSchemas: provision, vaultProvisioning: provision ? cloneProvision(vault, "vaultAdmin") : null, auditProvisioning: provision ? cloneProvision(writer, "auditAdmin") : null };
    }
    function notice(text, error = false) { $("setup-notice").hidden = false; $("setup-notice").textContent = text; $("setup-notice").className = "notice" + (error ? " error" : ""); }
    async function post(path) {
        const response = await fetch("/api/setup/" + path, { method: "POST", credentials: "same-origin", cache: "no-store", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf, "X-SETUP-TOKEN": value("code") }, body: JSON.stringify(request()) });
        const result = await response.json(); if (!response.ok) throw new Error(result.error || "Kontrollen kunde inte slutföras."); return result;
    }
    function show() {
        sections.forEach((s, i) => s.hidden = i !== step); Array.from($("steps").children).forEach((s, i) => s.classList.toggle("current", i === step));
        $("previous").disabled = step === 0; $("next").hidden = step === sections.length - 1;
    }
    const intro = section("Webbplats och installationskod", "Installationen kräver Windows-inloggning och den privata engångskoden från installationskommandot.");
    field(intro, "code", "Installationskod", "", "password", "Koden genereras på servern med --initialize-setup. Den förbrukas när installationen sparas.").maxLength = 64;
    field(intro, "hosts", "Webbplatsens DNS-namn", location.hostname, "text", "Exempel: valvra.example.se. Ange flera godkända namn med semikolon; inga jokertecken.");
    const databases = section("Anslut databaserna", "Två separata databaser behövs: Valvra och ValvraAudit. Skapa databaser och runtime-identiteter innan du fortsätter.");
    connection(databases, "vault", "Valvdatabas", "Valvra"); connection(databases, "writer", "Audit – skrivkoppling (endast INSERT)", "ValvraAudit"); connection(databases, "reader", "Audit – läskoppling (endast SELECT)", "ValvraAudit", "Windows");
    const readerIdentity = node("div", undefined, "connection-panel"); readerIdentity.append(node("h3", "Separat Windows-konto för auditläsning"), node("p", "Valv och auditskrivning använder tjänstens Windows-identitet. Auditläsaren behöver ett annat konto för att skrivaren ska behålla INSERT-only-rättigheter.", "hint"));
    field(readerIdentity, "readerWindowsDomain", "Windows-domän"); field(readerIdentity, "readerWindowsUser", "Läskontots användarnamn"); field(readerIdentity, "readerWindowsPassword", "Läskontots lösenord", "", "password", "Används endast för läskopplingen och sparas i DPAPI-skyddad konfiguration. Vid databasinloggning behövs inte dessa fält."); databases.append(readerIdentity);
    const provisioning = node("div", undefined, "connection-panel"); provisioning.append(node("h3", "Databasscheman")); const provision = field(provisioning, "provision", "Installera / uppdatera scheman med tillfälliga installationskonton", "", "checkbox");
    const adminFields = node("div"); [ ["vaultAdmin", "Valvets installationskonto"], ["auditAdmin", "Auditdatabasens installationskonto"] ].forEach(([prefix, title]) => {
        adminFields.append(node("h3", title)); select(adminFields, prefix + "Authentication", "Autentisering", [["Password", "Databaskonto och lösenord"], ["Windows", "Tjänstens Windows-identitet"]], "Password");
        field(adminFields, prefix + "Username", "Installationsanvändare"); field(adminFields, prefix + "Password", "Installationslösenord", "", "password");
    }); adminFields.hidden = true; provision.addEventListener("change", () => adminFields.hidden = !provision.checked); provisioning.append(adminFields, node("p", "Installationskontona sparas inte. Databaserna måste redan finnas. Ge runtime-kontona rätt roller enligt README; schemainstallation lägger inte automatiskt till konton i rollerna.", "hint")); databases.append(provisioning);
    const identity = section("Active Directory och certifikat", "Windows SSO används för inloggning. AD hämtar aktuella konton och gruppmedlemskap över LDAPS.");
    field(identity, "adServer", "Domänkontrollantens DNS-namn", "", "text", "LDAPS på port 636. Tjänsteidentiteten behöver katalogläsrätt."); field(identity, "baseDn", "Katalogens sökbas", "", "text", "Exempel: DC=example,DC=se");
    field(identity, "adminSid", "SID för behörighetsadministratörsgruppen", "", "text", "Ditt konto måste ingå i gruppen. Exempel: S-1-5-21-…"); field(identity, "auditorSid", "SID för auditläsargruppen");
    field(identity, "encryptionThumbprint", "Krypteringscertifikatets tumavtryck", "", "text", "LocalMachine/My, RSA minst 3072 bitar. Tjänsteidentiteten behöver privatnyckelåtkomst.");
    field(identity, "auditThumbprint", "Auditcertifikatets tumavtryck", "", "text", "Ett separat RSA-certifikat används för audithändelsernas signaturer.");
    field(identity, "oldEncryptionThumbprints", "Äldre krypteringscertifikat (valfritt)", "", "text", "Tumavtryck separerade med semikolon. Behåll nycklar som behövs för befintliga poster och backup.");
    field(identity, "oldAuditThumbprints", "Äldre publika auditcertifikat (valfritt)", "", "text", "Behåll tumavtryck för verifiering av äldre audit efter rotation.");
    identity.append(node("h3", "LDAP-test av sparade konton (valfritt)")); field(identity, "testServer", "Godkänd testserver (LDAPS)"); field(identity, "testName", "Profilnamn", "AD"); field(identity, "testDomain", "Kontodomän (NetBIOS)");
    const checks = section("Kontrollera installationen", "Testerna ansluter med de valda runtime-identiteterna och skriver en signerad installationshändelse i auditdatabasen.");
    const results = node("div"); const actions = node("div", undefined, "actions"); const validate = node("button", "Testa anslutningar och rättigheter", "button"); validate.type = "button";
    const finish = node("button", "Spara och slutför installationen", "button primary"); finish.type = "button"; finish.disabled = true; actions.append(validate, finish); checks.append(actions, results);
    validate.addEventListener("click", async () => {
        validate.disabled = finish.disabled = true; notice(fields.provision.checked ? "Förbereder scheman och testar säkerhetsinställningar…" : "Testar anslutningar och säkerhetsinställningar…");
        try { const result = await post("validate"); results.replaceChildren(); result.checks.forEach(check => { const row = node("div", undefined, "setup-check " + (check.passed ? "passed" : "failed")); row.append(node("strong", (check.passed ? "✓ " : "× ") + check.name), node("p", check.message)); results.append(row); });
            passed = result.passed; finish.disabled = !passed; if (passed) { fields.provision.checked = false; adminFields.hidden = true; fields.vaultAdminPassword.value = fields.auditAdminPassword.value = ""; } notice(passed ? "Alla kontroller passerade. Spara konfigurationen för att slutföra." : "Åtgärda de markerade kontrollerna och försök igen.", !passed);
        } catch (error) { notice(error.message, true); } finally { validate.disabled = false; }
    });
    finish.addEventListener("click", async () => {
        if (!passed) return; finish.disabled = validate.disabled = true;
        try { await post("finish"); Object.values(fields).forEach(input => { if (input.type === "password") input.value = ""; });
            $("setup-form").replaceChildren(); const complete = node("div", undefined, "setup-complete"); complete.append(node("h2", "Installationen är sparad"), node("p", "Starta om Valvras IIS-applikationspool. Öppna sedan webbplatsen igen för att använda valvet.")); const link = node("a", "Öppna Valvra", "button primary"); link.href = "/"; complete.append(link); $("setup-form").append(complete); notice("Engångskoden har förbrukats. Konfigurationen är krypterad på servern.");
        } catch (error) { notice(error.message, true); finish.disabled = validate.disabled = false; }
    });
    $("next").addEventListener("click", () => { step = Math.min(step + 1, sections.length - 1); show(); }); $("previous").addEventListener("click", () => { step = Math.max(0, step - 1); show(); });
    $("setup-form").addEventListener("submit", event => event.preventDefault()); $("setup-form").addEventListener("input", () => { passed = false; finish.disabled = true; });
    show();
    fetch("/api/setup/status", { credentials: "same-origin", cache: "no-store" }).then(response => { if (!response.ok) throw new Error("Windows-inloggningen kunde inte verifieras."); return response.json(); }).then(status => {
        csrf = status.csrfToken;
        if (!status.canConfigure) { $("setup-form").hidden = true; notice(status.installed ? "Valvra är redan konfigurerat. För att ändra installationen, generera en ny engångskod på servern enligt README." : "Generera en installationskod på servern enligt README."); }
    }).catch(error => notice(error.message, true));
})();
