"use strict";
(async () => {
    const $ = id => document.getElementById(id);
    const i18n = window.ValvraI18n;
    const t = (key, values) => i18n.message(key, values);
    await i18n.ready;
    const fields = {};
    let csrf = "", step = 0, passed = false;
    const sections = [];
    const requiredFields = new Set(["code", "hosts", "vaultServer", "vaultDatabase", "writerServer", "writerDatabase", "readerServer", "readerDatabase", "adServer", "baseDn", "encryptionThumbprint", "auditThumbprint", "integrityThumbprint"]);
    const node = (tag, text, className) => { const n = document.createElement(tag); if (text !== undefined) i18n.setText(n, text); if (className) n.className = className; return n; };
    function field(parent, key, label, value = "", type = "text", help = "") {
        const l = node("label"); l.append(node("span", label)); if (requiredFields.has(key)) l.append(node("span", t(" (obligatoriskt)"))); l.htmlFor = "setup-" + key;
        const input = node("input"); input.type = type; input.id = l.htmlFor; input.value = value; input.autocomplete = "off";
        input.required = requiredFields.has(key);
        input.addEventListener("input", () => input.removeAttribute("aria-invalid"));
        fields[key] = input; parent.append(l, input); if (help) { const description = node("p", help, "hint"); description.id = input.id + "-help"; input.setAttribute("aria-describedby", description.id); parent.append(description); } return input;
    }
    function select(parent, key, label, options, value) {
        const l = node("label", label); l.htmlFor = "setup-" + key; const input = node("select"); input.id = l.htmlFor;
        options.forEach(([v, text]) => { const o = node("option", text); o.value = v; input.append(o); }); if (value) input.value = value;
        fields[key] = input; parent.append(l, input); return input;
    }
    function section(title, text) { const container = node("section", undefined, "setup-fields-section"); const heading = node("h2", title); heading.id = "step-" + sections.length; heading.tabIndex = -1; container.setAttribute("aria-labelledby", heading.id); container.append(heading, node("p", text, "hint")); sections.push(container); $("setup-fields").append(container); return container; }
    function connection(parent, prefix, title, database, authentication = "Windows") {
        const card = node("fieldset", undefined, "connection-panel"); card.append(node("legend", title));
        select(card, prefix + "Provider", t("Databastyp"), [["SqlServer", "Microsoft SQL Server"], ["PostgreSql", "PostgreSQL"]], "SqlServer");
        field(card, prefix + "Server", t("Databasserver"), "", "text", t("Använd serverns fullständiga DNS-namn. Ett betrott TLS-certifikat krävs."));
        field(card, prefix + "Database", t("Databasnamn"), database);
        field(card, prefix + "Port", t("PostgreSQL-port"), "5432", "number");
        select(card, prefix + "Authentication", t("Autentisering"), [["Windows", "Windows / Integrated Security"], ["Password", t("Databaskonto och lösenord")]], authentication);
        field(card, prefix + "Username", t("Databasanvändare (vid lösenord eller PostgreSQL-roll)"));
        field(card, prefix + "Password", t("Databaslösenord"), "", "password");
        const update = () => { fields[prefix + "Password"].disabled = fields[prefix + "Authentication"].value === "Windows"; fields[prefix + "Port"].disabled = fields[prefix + "Provider"].value !== "PostgreSql"; };
        fields[prefix + "Authentication"].addEventListener("change", update); fields[prefix + "Provider"].addEventListener("change", update); update(); parent.append(card);
    }
    function value(key) { return fields[key].value.trim(); }
    function database(prefix) { return { provider: value(prefix + "Provider"), server: value(prefix + "Server"), database: value(prefix + "Database"), port: Number(value(prefix + "Port")),
        authentication: value(prefix + "Authentication"), username: value(prefix + "Username"), password: fields[prefix + "Password"].value }; }
    function request() {
        const vault = database("vault"), writer = database("writer"), separateReader = fields.separateReader.checked, reader = separateReader ? database("reader") : writer;
        const provision = fields.provision.checked;
        const cloneProvision = (db, prefix) => ({ ...db, authentication: value(prefix + "Authentication"), username: value(prefix + "Username"), password: fields[prefix + "Password"].value });
        return { allowedHosts: value("hosts"), vault, auditWriter: writer, auditReader: reader, useSeparateAuditReader: separateReader,
            auditReaderWindowsCredentials: separateReader && reader.authentication === "Windows" && value("readerWindowsUser") ? { domain: value("readerWindowsDomain"), username: value("readerWindowsUser"), password: fields.readerWindowsPassword.value } : null,
            keyProtection: { activeThumbprint: value("encryptionThumbprint"), allowedThumbprints: value("oldEncryptionThumbprints").split(/[;,\s]+/).filter(Boolean) }, auditSigningThumbprint: value("auditThumbprint"),
            auditVerificationThumbprints: value("oldAuditThumbprints").split(/[;,\s]+/).filter(Boolean),
            integritySigningThumbprint: value("integrityThumbprint"),
            integrityVerificationThumbprints: value("oldIntegrityThumbprints").split(/[;,\s]+/).filter(Boolean),
            identity: {loginProviderId: "windows", directoryProviderId: "active-directory"},
            activeDirectory: { server: value("adServer"), port: 636, baseDn: value("baseDn"), userSearchBaseDn: value("userBaseDn"), groupSearchBaseDn: value("groupBaseDn"), timeoutSeconds: 10 },
            ldapTests: { profiles: value("testServer") ? [{ id: "default", name: value("testName") || "AD", server: value("testServer"), port: 636, domain: value("testDomain"), timeoutSeconds: 5 }] : [] },
            provisionSchemas: provision, vaultProvisioning: provision ? cloneProvision(vault, "vaultAdmin") : null, auditProvisioning: provision ? cloneProvision(writer, "auditAdmin") : null };
    }
    function notice(text, error = false) { const target = $(error ? "setup-error" : "setup-notice"); $(error ? "setup-notice" : "setup-error").hidden = true; target.hidden = false; i18n.setText(target, text); }
    async function post(path) {
        const response = await fetch("/api/setup/" + path, { method: "POST", credentials: "same-origin", cache: "no-store", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf, "X-SETUP-TOKEN": value("code"), "X-Valvra-Language": i18n.language }, body: JSON.stringify(request()) });
        const result = await response.json(); if (!response.ok) throw new Error(result.error || t("Kontrollen kunde inte slutföras.")); return result;
    }
    function show(focus = false) {
        sections.forEach((s, i) => s.hidden = i !== step); Array.from($("steps").children).forEach((s, i) => { s.classList.toggle("current", i === step); if (i === step) s.setAttribute("aria-current", "step"); else s.removeAttribute("aria-current"); });
        $("previous").disabled = step === 0; $("next").hidden = step === sections.length - 1;
        if (focus) sections[step].querySelector("h2").focus();
    }
    const intro = section(t("Webbplats och installationskod"), t("Installationen kräver Windows-inloggning och den privata engångskoden från installationskommandot."));
    field(intro, "code", t("Installationskod"), "", "password", t("Koden genereras på servern med --initialize-setup. Den förbrukas när installationen sparas.")).maxLength = 64;
    field(intro, "hosts", t("Webbplatsens DNS-namn"), location.hostname, "text", t("Exempel: valvra.example.se. Ange flera godkända namn med semikolon; inga jokertecken."));
    const databases = section(t("Anslut databaserna"), t("Två separata databaser behövs: Valvra och ValvraAudit. Skapa databaser och runtime-identiteter innan du fortsätter."));
    connection(databases, "vault", t("Valvdatabas"), "Valvra"); connection(databases, "writer", t("Auditdatabas (SELECT och INSERT)"), "ValvraAudit");
    databases.append(node("p", t("Samma tjänstekonto används för läsning och tillägg i audit. Med dMSA/gMSA och Integrated Security sparas inget kontolösenord i Valvra. Kontot får inte ändra eller radera audit."), "hint"));
    const separateReader = field(databases, "separateReader", t("Använd separat auditläsare (valfritt)"), "", "checkbox");
    const readerFields = node("div"); connection(readerFields, "reader", t("Audit – läskoppling (endast SELECT)"), "ValvraAudit", "Windows");
    const readerIdentity = node("div", undefined, "connection-panel"); readerIdentity.append(node("h3", t("Separat Windows-konto för auditläsning")), node("p", t("Endast vid separat läskonto: ange Windows-inloggning nedan eller välj ett databaskonto i läskopplingen."), "hint"));
    field(readerIdentity, "readerWindowsDomain", t("Windows-domän")); field(readerIdentity, "readerWindowsUser", t("Läskontots användarnamn")); field(readerIdentity, "readerWindowsPassword", t("Läskontots lösenord"), "", "password", t("Används endast för läskopplingen och sparas i DPAPI-skyddad konfiguration. Vid databasinloggning behövs inte dessa fält.")); readerFields.append(readerIdentity); databases.append(readerFields);
    const updateReader = () => {
        readerFields.hidden = !separateReader.checked;
        readerFields.querySelectorAll("input,select").forEach(input => input.disabled = !separateReader.checked);
        if (separateReader.checked) {
            fields.readerPassword.disabled = fields.readerAuthentication.value === "Windows";
            fields.readerPort.disabled = fields.readerProvider.value !== "PostgreSql";
        } else readerFields.querySelectorAll('input[type="password"]').forEach(input => input.value = "");
    };
    separateReader.addEventListener("change", updateReader); updateReader();
    const provisioning = node("div", undefined, "connection-panel"); provisioning.append(node("h3", t("Databasscheman"))); const provision = field(provisioning, "provision", t("Installera / uppdatera scheman med tillfälliga installationskonton"), "", "checkbox");
    const adminFields = node("div"); [ ["vaultAdmin", t("Valvets installationskonto")], ["auditAdmin", t("Auditdatabasens installationskonto")] ].forEach(([prefix, title]) => {
        const account = node("fieldset"); account.append(node("legend", title)); adminFields.append(account); select(account, prefix + "Authentication", t("Autentisering"), [["Password", t("Databaskonto och lösenord")], ["Windows", t("Tjänstens Windows-identitet")]], "Password");
        field(account, prefix + "Username", t("Installationsanvändare")); field(account, prefix + "Password", t("Installationslösenord"), "", "password");
    }); adminFields.hidden = true; provision.addEventListener("change", () => adminFields.hidden = !provision.checked); provisioning.append(adminFields, node("p", t("Installationskontona sparas inte. Databaserna måste redan finnas. Ge runtime-kontona rätt roller enligt README; schemainstallation lägger inte automatiskt till konton i rollerna."), "hint")); databases.append(provisioning);
    const identity = section(t("Active Directory och certifikat"), t("Windows SSO används för inloggning. AD hämtar aktuella konton och gruppmedlemskap över LDAPS."));
    field(identity, "adServer", t("Domänkontrollantens DNS-namn"), "", "text", t("LDAPS på port 636. Tjänsteidentiteten behöver katalogläsrätt.")); field(identity, "baseDn", t("Katalogens sökbas"), "", "text", t("Exempel: DC=example,DC=se"));
    field(identity, "userBaseDn", t("Sökbas för personkonton (valfritt)")); field(identity, "groupBaseDn", t("Sökbas för behörighetsgrupper (valfritt)"));
    identity.append(node("p", t("Personkontot som slutför installationen får behörighets- och systemadministration. Globala rättigheter tilldelas därefter personkonton under Inställningar."), "hint"));
    field(identity, "encryptionThumbprint", t("Krypteringscertifikatets tumavtryck"), "", "text", t("LocalMachine/My, RSA minst 3072 bitar. Tjänsteidentiteten behöver privatnyckelåtkomst."));
    field(identity, "auditThumbprint", t("Auditcertifikatets tumavtryck"), "", "text", t("Ett separat RSA-certifikat används för audithändelsernas signaturer."));
    field(identity, "integrityThumbprint", t("Integritetscertifikatets tumavtryck"), "", "text", t("Ett tredje RSA-certifikat skyddar valvets kontrollpunkt utanför databasen. LocalMachine/My, minst 3072 bitar."));
    field(identity, "oldEncryptionThumbprints", t("Äldre krypteringscertifikat (valfritt)"), "", "text", t("Tumavtryck separerade med semikolon. Behåll nycklar som behövs för befintliga poster och backup."));
    field(identity, "oldAuditThumbprints", t("Äldre publika auditcertifikat (valfritt)"), "", "text", t("Behåll tumavtryck för verifiering av äldre audit efter rotation."));
    field(identity, "oldIntegrityThumbprints", t("Äldre publika integritetscertifikat (valfritt)"), "", "text", t("Behåll föregående certifikat vid rotation och för verifiering av backupens kontrollpunkt."));
    identity.append(node("h3", t("LDAP-test av sparade konton (valfritt)"))); field(identity, "testServer", t("Godkänd testserver (LDAPS)")); field(identity, "testName", t("Profilnamn"), "AD"); field(identity, "testDomain", t("Kontodomän (NetBIOS)"));
    const checks = section(t("Kontrollera installationen"), t("Testerna ansluter med de valda runtime-identiteterna och skriver en signerad installationshändelse i auditdatabasen."));
    const results = node("div"); results.setAttribute("role", "region"); results.setAttribute("aria-label", t("Testresultat")); const actions = node("div", undefined, "actions"); const validate = node("button", t("Testa anslutningar och rättigheter"), "button"); validate.type = "button";
    const finish = node("button", t("Spara och slutför installationen"), "button primary"); finish.type = "button"; finish.disabled = true; actions.append(validate, finish); checks.append(actions, results);
    validate.addEventListener("click", async () => {
        const invalid = Object.values(fields).find(input => !input.checkValidity());
        if (invalid) { step = sections.findIndex(s => s.contains(invalid)); show(); invalid.reportValidity(); return; }
        validate.disabled = finish.disabled = true; results.setAttribute("aria-busy", "true"); notice(fields.provision.checked ? t("Förbereder scheman och testar säkerhetsinställningar…") : t("Testar anslutningar och säkerhetsinställningar…"));
        try { const result = await post("validate"); results.replaceChildren(); result.checks.forEach(check => { const row = node("div", undefined, "setup-check " + (check.passed ? "passed" : "failed")); const label = node("strong"); label.append(node("span", check.passed ? t("Godkänd: ") : t("Åtgärda: ")), node("span", t(check.nameKey ?? check.name))); row.append(label, node("p", t(check.messageKey ?? check.message))); results.append(row); });
            passed = result.passed; finish.disabled = !passed; if (passed) { fields.provision.checked = false; adminFields.hidden = true; fields.vaultAdminPassword.value = fields.auditAdminPassword.value = ""; } notice(passed ? t("Alla kontroller passerade. Spara konfigurationen för att slutföra.") : t("Åtgärda de markerade kontrollerna och försök igen."), !passed);
        } catch (error) { notice(error.message, true); } finally { validate.disabled = false; results.setAttribute("aria-busy", "false"); }
    });
    finish.addEventListener("click", async () => {
        if (!passed) return; finish.disabled = validate.disabled = true;
        try { await post("finish"); Object.values(fields).forEach(input => { if (input.type === "password") input.value = ""; });
            $("setup-form").replaceChildren(); const complete = node("div", undefined, "setup-complete"); const heading = node("h2", t("Installationen är sparad")); heading.tabIndex = -1; complete.append(heading, node("p", t("Starta om Valvras IIS-applikationspool. Öppna sedan webbplatsen igen för att använda valvet."))); const link = node("a", t("Öppna Valvra"), "button primary"); link.href = "/"; complete.append(link); $("setup-form").append(complete); heading.focus(); notice(t("Engångskoden har förbrukats. Konfigurationen är krypterad på servern."));
        } catch (error) { notice(error.message, true); finish.disabled = validate.disabled = false; }
    });
    $("next").addEventListener("click", () => { const invalid = Array.from(sections[step].querySelectorAll("input,select")).find(input => !input.checkValidity()); if (invalid) { invalid.reportValidity(); return; } step = Math.min(step + 1, sections.length - 1); show(true); }); $("previous").addEventListener("click", () => { step = Math.max(0, step - 1); show(true); });
    $("setup-form").addEventListener("invalid", event => { event.target.setAttribute("aria-invalid", "true"); notice(t("Kontrollera fältet ”{field}”. {reason}", {field: event.target.labels[0].textContent, reason: i18n.validation(event.target)}), true); }, true);
    $("setup-form").addEventListener("submit", event => event.preventDefault()); $("setup-form").addEventListener("input", () => { passed = false; finish.disabled = true; });
    show();
    fetch("/api/setup/status", { credentials: "same-origin", cache: "no-store", headers: {"X-Valvra-Language": i18n.language} }).then(response => { if (!response.ok) throw new Error(t("Windows-inloggningen kunde inte verifieras.")); return response.json(); }).then(status => {
        csrf = status.csrfToken;
        if (!status.canConfigure) { $("setup-form").hidden = true; notice(status.installed ? t("Valvra är redan konfigurerat. För att ändra installationen, generera en ny engångskod på servern enligt README.") : t("Generera en installationskod på servern enligt README.")); }
    }).catch(error => notice(error.message, true));
})().catch(error => {
    const target = document.getElementById("setup-error"); target.hidden = false;
    target.textContent = error.message || "Could not load setup / Kunde inte läsa installationen.";
});
