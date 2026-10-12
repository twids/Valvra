"use strict";
(async () => {
    const $ = id => document.getElementById(id);
    const i18n = window.ValvraI18n;
    const t = (key, values) => i18n.message(key, values);
    await i18n.ready;
    const fields = {};
    let csrf = "", step = 0, passed = false, revision = 0;
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
    function guidance(parent, text, links) {
        const panel = node("aside", undefined, "setup-guidance");
        panel.append(node("p", t(text)), node("p", t("Guiderna finns på GitHub på engelska och öppnas i en ny flik. Dina inmatade värden finns kvar här."), "hint"));
        const list = node("ul");
        for (const [label, path] of links) {
            const item = node("li"), link = node("a", t(label));
            link.href = "https://github.com/twids/Valvra/blob/master/" + path;
            link.target = "_blank"; link.rel = "noopener noreferrer";
            item.append(link); list.append(item);
        }
        panel.append(list); parent.append(panel);
    }
    function updateCredentials(parent, prefix, postgres) {
        const integrated = fields[prefix + "Authentication"].value === "Windows";
        for (const suffix of ["Username", "Password"]) {
            const input = fields[prefix + suffix];
            input.disabled = integrated && (suffix === "Password" || !postgres);
            input.hidden = input.disabled;
            parent.querySelector('label[for="' + input.id + '"]').hidden = input.hidden;
            if (input.disabled) input.value = "";
        }
    }
    function connection(parent, prefix, title, database, authentication = "Windows") {
        const card = node("fieldset", undefined, "connection-panel"); card.append(node("legend", title));
        select(card, prefix + "Provider", t("Databastyp"), [["SqlServer", "Microsoft SQL Server"], ["PostgreSql", "PostgreSQL"]], "SqlServer");
        field(card, prefix + "Server", t("Databasserver"), "", "text", t("Använd serverns fullständiga DNS-namn. Ett betrott TLS-certifikat krävs."));
        field(card, prefix + "Database", t("Databasnamn"), database);
        field(card, prefix + "Port", t("PostgreSQL-port"), "5432", "number");
        select(card, prefix + "Authentication", t("Autentisering"), [["Windows", t("Aktuellt tjänstekonto (Integrated Security)")], ["Password", t("Databaskonto och lösenord")]], authentication);
        if (prefix !== "reader") card.append(node("p", t("Integrated Security använder Windows-kontot som kör Valvra (IIS-apppoolens tjänstekonto), inte personen som använder installationsguiden. Inget kontolösenord behöver anges."), "hint"));
        field(card, prefix + "Username", t("Databasanvändare (vid lösenord eller PostgreSQL-roll)"));
        field(card, prefix + "Password", t("Databaslösenord"), "", "password");
        const advanced = optional(card, t("Avancerade anslutningsval"));
        const timeout = field(advanced, prefix + "ConnectTimeoutSeconds", t("Anslutningstimeout (sekunder)"), "10", "number", t("1–120 sekunder. Gäller anslutningen, inte tiden för databasfrågor."));
        timeout.min = "1"; timeout.max = "120"; timeout.required = true;
        const sqlOptions = node("div"); advanced.append(sqlOptions);
        field(sqlOptions, prefix + "TrustServerCertificate", "Trust server certificate", "", "checkbox", t("Anslutningen krypteras, men servercertifikatets giltighet och namn kontrolleras inte. Använd endast för en server du litar på. Avmarkerat som standard."));
        field(sqlOptions, prefix + "MultiSubnetFailover", "MultiSubnetFailover", "", "checkbox", t("För SQL Server Always On eller failover-kluster via TCP. Avmarkerat som standard."));
        const update = () => {
            const postgres = fields[prefix + "Provider"].value === "PostgreSql";
            sqlOptions.hidden = postgres;
            for (const suffix of ["TrustServerCertificate", "MultiSubnetFailover"]) {
                fields[prefix + suffix].disabled = postgres;
                if (postgres) fields[prefix + suffix].checked = false;
            }
            updateCredentials(card, prefix, postgres);
            const port = fields[prefix + "Port"];
            port.disabled = !postgres;
            port.hidden = !postgres;
            card.querySelector('label[for="' + port.id + '"]').hidden = !postgres;
            i18n.setText(card.querySelector("#setup-" + prefix + "Server-help"), postgres
                ? t("Använd serverns fullständiga DNS-namn. Ett betrott TLS-certifikat krävs.")
                : t("SQL Server: ange serverns DNS-namn, server\\instans eller server,port. Certifikatval finns under avancerade anslutningsval."));
        };
        fields[prefix + "Authentication"].addEventListener("change", update); fields[prefix + "Provider"].addEventListener("change", update); update(); parent.append(card);
    }
    function value(key) { return fields[key].value.trim(); }
    function database(prefix) { return { provider: value(prefix + "Provider"), server: value(prefix + "Server"), database: value(prefix + "Database"), port: Number(value(prefix + "Port")),
        trustServerCertificate: value(prefix + "Provider") === "SqlServer" && fields[prefix + "TrustServerCertificate"].checked,
        multiSubnetFailover: value(prefix + "Provider") === "SqlServer" && fields[prefix + "MultiSubnetFailover"].checked,
        connectTimeoutSeconds: Number(value(prefix + "ConnectTimeoutSeconds")),
        authentication: value(prefix + "Authentication"), username: fields[prefix + "Username"].disabled ? "" : value(prefix + "Username"), password: fields[prefix + "Password"].disabled ? "" : fields[prefix + "Password"].value }; }
    function request() {
        const vault = database("vault"), writer = database("writer"), separateReader = fields.separateReader.checked, reader = separateReader ? database("reader") : writer;
        const provision = fields.provision.checked;
        const cloneProvision = (db, prefix) => ({ ...db, authentication: value(prefix + "Authentication"), username: fields[prefix + "Username"].disabled ? "" : value(prefix + "Username"), password: fields[prefix + "Password"].disabled ? "" : fields[prefix + "Password"].value });
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
    async function post(path, body = request()) {
        const response = await fetch("/api/setup/" + path, { method: "POST", credentials: "same-origin", cache: "no-store", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf, "X-SETUP-TOKEN": value("code"), "X-Valvra-Language": i18n.language }, body: JSON.stringify(body) });
        const result = await response.json();
        if (!response.ok) {
            const error = new Error(result.error || t("Kontrollen kunde inte slutföras."));
            error.checks = result.checks; throw error;
        }
        return result;
    }
    const excludedDraftFields = new Set(["code", "vaultPassword", "writerPassword", "readerPassword", "readerWindowsPassword", "vaultAdminPassword", "auditAdminPassword"]);
    function draft() {
        return { step, fields: Object.fromEntries(Object.entries(fields)
            .filter(([key, input]) => !excludedDraftFields.has(key) && input.type !== "password")
            .map(([key, input]) => [key, input.type === "checkbox" ? String(input.checked) : input.value])) };
    }
    async function saveDraft() { await post("draft", draft()); }
    function show(focus = false) {
        sections.forEach((s, i) => s.hidden = i !== step); Array.from($("steps").children).forEach((s, i) => { s.classList.toggle("current", i === step); const button = s.querySelector("button"); if (i === step) button.setAttribute("aria-current", "step"); else button.removeAttribute("aria-current"); });
        $("previous").disabled = step === 0; $("next").hidden = step === sections.length - 1;
        if (step === sections.length - 1) renderReview();
        if (focus) sections[step].querySelector("h2").focus();
    }
    const intro = section(t("Webbplats och installationskod"), t("Installationen kräver Windows-inloggning och den privata engångskoden från installationskommandot."));
    guidance(document.querySelector(".setup-intro"), "Förbered först IIS, Windows-inloggning och tjänstekontot. Installationsskriptet visar engångskoden och certifikatens tumavtryck.", [
        ["Förbered servern och kör installationsskriptet", "README.md#installation--windows-and-iis"]
    ]);
    field(intro, "code", t("Installationskod"), "", "password", t("Koden genereras på servern med --initialize-setup. Den förbrukas när installationen sparas.")).maxLength = 64;
    field(intro, "hosts", t("Webbplatsens DNS-namn"), location.hostname, "text", t("Exempel: valvra.example.se. Ange flera godkända namn med semikolon; inga jokertecken."));
    const databases = section(t("Anslut databaserna"), t("Två separata databaser behövs: Valvra och ValvraAudit. Skapa databaser och runtime-identiteter innan du fortsätter."));
    guidance(databases, "Förbered databasernas TLS och konton enligt databasguiden. Schemainstallationen nedan skapar inte databaser eller tilldelar runtime-kontona roller.", [
        ["Databaser, Windows-inloggning och databasroller", "docs/DATABASES.md"],
        ["Auditbehörigheter och SQL-skript", "docs/AUDIT.md"]
    ]);
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
            updateCredentials(readerFields, "reader", fields.readerProvider.value === "PostgreSql");
            fields.readerPort.disabled = fields.readerProvider.value !== "PostgreSql";
            for (const suffix of ["TrustServerCertificate", "MultiSubnetFailover"]) fields["reader" + suffix].disabled = fields.readerProvider.value !== "SqlServer";
        } else readerFields.querySelectorAll('input[type="password"]').forEach(input => input.value = "");
    };
    separateReader.addEventListener("change", updateReader); updateReader();
    const provisioning = node("div", undefined, "connection-panel"); provisioning.append(node("h3", t("Databasscheman"))); const provision = field(provisioning, "provision", t("Installera / uppdatera scheman med tillfälliga installationskonton"), "", "checkbox");
    const adminFields = node("div"); [ ["vaultAdmin", t("Valvets installationskonto")], ["auditAdmin", t("Auditdatabasens installationskonto")] ].forEach(([prefix, title]) => {
        const account = node("fieldset"); account.append(node("legend", title)); adminFields.append(account); select(account, prefix + "Authentication", t("Autentisering"), [["Password", t("Databaskonto och lösenord")], ["Windows", t("Aktuellt tjänstekonto (Integrated Security)")]], "Password");
        field(account, prefix + "Username", t("Installationsanvändare")); field(account, prefix + "Password", t("Installationslösenord"), "", "password");
        const databasePrefix = prefix === "vaultAdmin" ? "vault" : "writer";
        const update = () => updateCredentials(account, prefix, fields[databasePrefix + "Provider"].value === "PostgreSql");
        fields[prefix + "Authentication"].addEventListener("change", update);
        fields[databasePrefix + "Provider"].addEventListener("change", update); update();
    }); adminFields.hidden = true; provision.addEventListener("change", () => adminFields.hidden = !provision.checked); provisioning.append(adminFields, node("p", t("Installationskontona sparas inte. Databaserna måste redan finnas. Ge runtime-kontona rätt roller enligt databasguiden ovan; schemainstallation lägger inte automatiskt till konton i rollerna."), "hint")); databases.append(provisioning);
    const testDatabases = node("button", t("Testa databaser"), "button"); testDatabases.type = "button";
    const databaseResults = node("div"); databaseResults.id = "setup-database-results"; databaseResults.setAttribute("role", "region"); databaseResults.setAttribute("aria-label", t("Testresultat")); databaseResults.setAttribute("aria-live", "polite");
    databases.append(testDatabases, node("p", t("Testar anslutning och autentisering med valda konton. Scheman och rättigheter kontrolleras i sista steget."), "hint"), databaseResults);
    let databaseRevision = 0;
    const resetDatabaseResults = () => { databaseRevision++; databaseResults.replaceChildren(); };
    databases.addEventListener("input", resetDatabaseResults); databases.addEventListener("change", resetDatabaseResults);
    fields.code.addEventListener("input", resetDatabaseResults);
    testDatabases.addEventListener("click", async () => {
        const invalid = [fields.code, ...databases.querySelectorAll("input,select")].find(input => !input.checkValidity());
        if (invalid) { step = sections.findIndex(s => s.contains(invalid)); show(); invalid.reportValidity(); return; }
        const revision = databaseRevision;
        const {vault, auditWriter, auditReader, useSeparateAuditReader, auditReaderWindowsCredentials, provisionSchemas, vaultProvisioning, auditProvisioning} = request();
        testDatabases.disabled = true; databaseResults.setAttribute("aria-busy", "true");
        databaseResults.replaceChildren(node("p", t("Testar databasanslutningar…")));
        try {
            await saveDraft();
            const result = await post("test-databases", {vault, auditWriter, auditReader, useSeparateAuditReader, auditReaderWindowsCredentials, provisionSchemas, vaultProvisioning, auditProvisioning});
            if (revision === databaseRevision) renderChecks(result.checks, databaseResults);
        } catch (error) {
            if (revision === databaseRevision) databaseResults.replaceChildren(node("p", error.message));
        } finally { testDatabases.disabled = false; databaseResults.setAttribute("aria-busy", "false"); }
    });
    function optional(parent, title) {
        const details = node("details", undefined, "connection-panel"); details.append(node("summary", title)); parent.append(details); return details;
    }
    const identity = section(t("Identitet och Active Directory"), t("Windows SSO används för inloggning. AD hämtar aktuella konton och gruppmedlemskap över LDAPS."));
    identity.append(node("p", t("Du loggar in med ditt personkonto i webbläsaren. Valvras IIS-tjänstekonto ansluter till katalogen och läser användare och grupper."), "setup-guidance"));
    guidance(identity, "Ange domänkontrollanten och sökbasen som omfattar dina användare och grupper. Katalogen kontrolleras från servern i sista steget.", [
        ["Active Directory, tjänstekonto och Windows-inloggning", "README.md#1-prepare-the-server"]
    ]);
    field(identity, "adServer", t("Domänkontrollantens DNS-namn"), "", "text", t("LDAPS på port 636. Tjänsteidentiteten behöver katalogläsrätt.")); field(identity, "baseDn", t("Katalogens sökbas"), "", "text", t("Exempel: DC=example,DC=se"));
    const searchBases = optional(identity, t("Begränsa katalogsökning (valfritt)"));
    searchBases.append(node("p", t("Lämna tomt för att använda katalogens sökbas för både personkonton och grupper."), "hint"));
    field(searchBases, "userBaseDn", t("Sökbas för personkonton (valfritt)")); field(searchBases, "groupBaseDn", t("Sökbas för behörighetsgrupper (valfritt)"));
    identity.append(node("p", t("Personkontot som slutför installationen får behörighets- och systemadministration. Globala rättigheter tilldelas därefter personkonton under Inställningar."), "hint"));
    const ldapTests = optional(identity, t("LDAP-test av sparade konton (valfritt)"));
    ldapTests.append(node("p", t("Detta skapar en profil för senare test av sparade konton. Det behövs inte för Windows-inloggning eller katalogsökning."), "hint"));
    field(ldapTests, "testServer", t("Godkänd testserver (LDAPS)")); field(ldapTests, "testName", t("Profilnamn"), "AD"); field(ldapTests, "testDomain", t("Kontodomän (NetBIOS)"));
    const certificates = section(t("Certifikat och nyckelskydd"), t("Tre olika certifikat skyddar valvet, auditloggen och kontrollpunkten. Använd tumavtrycken från installationsskriptet."));
    guidance(certificates, "Alla tre certifikat ska finnas i LocalMachine/My på IIS-servern. Valvras tjänstekonto behöver åtkomst till deras privata nycklar. Dessa är separata från webbplatsens HTTPS- och katalogens LDAPS-certifikat.", [
        ["Installationsskript och certifikat", "README.md#2-publish-and-install"]
    ]);
    function certificate(key, title, label, help) {
        const card = node("fieldset", undefined, "connection-panel"); card.append(node("legend", title)); certificates.append(card);
        field(card, key, label, "", "text", help);
    }
    certificate("encryptionThumbprint", t("Skydda hemligheterna"), t("Krypteringscertifikatets tumavtryck"), t("LocalMachine/My, RSA minst 3072 bitar. Tjänsteidentiteten behöver privatnyckelåtkomst."));
    certificate("auditThumbprint", t("Signera auditloggen"), t("Auditcertifikatets tumavtryck"), t("Ett separat RSA-certifikat används för audithändelsernas signaturer."));
    certificate("integrityThumbprint", t("Skydda valvets kontrollpunkt"), t("Integritetscertifikatets tumavtryck"), t("Ett tredje RSA-certifikat skyddar valvets kontrollpunkt utanför databasen. LocalMachine/My, minst 3072 bitar."));
    const oldCertificates = optional(certificates, t("Äldre certifikat vid rotation eller återställning (valfritt)"));
    field(oldCertificates, "oldEncryptionThumbprints", t("Äldre krypteringscertifikat (valfritt)"), "", "text", t("Tumavtryck separerade med semikolon. Behåll nycklar som behövs för befintliga poster och backup."));
    field(oldCertificates, "oldAuditThumbprints", t("Äldre publika auditcertifikat (valfritt)"), "", "text", t("Behåll tumavtryck för verifiering av äldre audit efter rotation."));
    field(oldCertificates, "oldIntegrityThumbprints", t("Äldre publika integritetscertifikat (valfritt)"), "", "text", t("Behåll föregående certifikat vid rotation och för verifiering av backupens kontrollpunkt."));
    const checks = section(t("Kontrollera installationen"), t("Testerna ansluter med de valda runtime-identiteterna och skriver en signerad installationshändelse i auditdatabasen."));
    guidance(checks, "Godkända kontroller här verifierar inte IIS Extended Protection eller att backup går att återställa. Följ installationskontrollerna och förbered återställning innan produktionsbruk.", [
        ["Installationskontroller i din miljö", "docs/VERIFICATION.md#installation-checks-in-your-own-environment"],
        ["Backup och återställning", "docs/OPERATIONS.md#what-must-be-backed-up"]
    ]);
    const results = node("div"); results.setAttribute("role", "region"); results.setAttribute("aria-label", t("Testresultat")); const actions = node("div", undefined, "actions"); const validate = node("button", t("Testa anslutningar och rättigheter"), "button"); validate.type = "button";
    const finish = node("button", t("Spara och slutför installationen"), "button primary"); finish.type = "button"; finish.disabled = true; actions.append(validate, finish); checks.append(actions, results);
    const review = node("div", undefined, "setup-review"); checks.insertBefore(review, actions);
    function goTo(index) { step = index; $("setup-error").hidden = $("setup-notice").hidden = true; show(true); }
    function editButton(index) {
        const button = node("button", t("Ändra {section}", {section: sections[index].querySelector("h2").textContent}), "button");
        button.type = "button"; button.addEventListener("click", () => goTo(index)); return button;
    }
    function renderReview() {
        review.replaceChildren(node("h3", t("Granska dina uppgifter")), node("p", t("Uppgifterna nedan är inte verifierade förrän serverkontrollerna har passerat. Lösenord och installationskod visas inte."), "hint"));
        const authenticationSummary = prefix => value(prefix + "Authentication") !== "Windows" ? value(prefix + "Username")
            : prefix === "reader" && value("readerWindowsUser") ? [value("readerWindowsDomain"), value("readerWindowsUser")].filter(Boolean).join("\\")
            : t("Aktuellt tjänstekonto (Integrated Security)");
        const rows = [
            [[t("Webbplatsens DNS-namn"), value("hosts")]],
            ["vault", "writer", ...(fields.separateReader.checked ? ["reader"] : [])].map(prefix => [t(prefix === "vault" ? "Valvdatabas" : prefix === "writer" ? "Auditdatabas (SELECT och INSERT)" : "Audit – läskoppling (endast SELECT)"),
                [value(prefix + "Provider"), value(prefix + "Server"), value(prefix + "Database"), value(prefix + "Provider") === "PostgreSql" ? value(prefix + "Port") : "", authenticationSummary(prefix), value(prefix + "Authentication") === "Windows" && value(prefix + "Provider") === "PostgreSql" ? value(prefix + "Username") : ""].filter(Boolean).join(" · ")]),
            [[t("Domänkontrollantens DNS-namn"), value("adServer")], [t("Katalogens sökbas"), value("baseDn")],
                [t("Sökbas för personkonton (valfritt)"), value("userBaseDn") || value("baseDn")], [t("Sökbas för behörighetsgrupper (valfritt)"), value("groupBaseDn") || value("baseDn")],
                [t("Godkänd testserver (LDAPS)"), value("testServer") ? [value("testServer"), value("testName") || "AD", value("testDomain")].filter(Boolean).join(" · ") : t("Inte konfigurerat")]],
            ["encryptionThumbprint", "auditThumbprint", "integrityThumbprint", "oldEncryptionThumbprints", "oldAuditThumbprints", "oldIntegrityThumbprints"].filter(key => fields[key].required || value(key)).map(key => [fields[key].labels[0].textContent, value(key)])
        ];
        rows.forEach((entries, index) => {
            const card = node("section", undefined, "connection-panel"); card.append(node("h3", sections[index].querySelector("h2").textContent), editButton(index));
            const list = node("dl"); entries.forEach(([label, text]) => list.append(node("dt", label), node("dd", text || t("Saknas")))); card.append(list);
            if (index === 1) {
                for (const prefix of ["vault", "writer", ...(fields.separateReader.checked ? ["reader"] : [])]) {
                    const db = database(prefix);
                    card.append(node("p", [t(prefix === "vault" ? "Valvdatabas" : prefix === "writer" ? "Auditdatabas (SELECT och INSERT)" : "Audit – läskoppling (endast SELECT)"),
                        t("Anslutningstimeout (sekunder)") + ": " + db.connectTimeoutSeconds,
                        db.provider === "SqlServer" ? "TrustServerCertificate=" + db.trustServerCertificate + "; MultiSubnetFailover=" + db.multiSubnetFailover : "SSL Mode=VerifyFull"].join(" · "), "hint"));
                }
                card.append(node("p", fields.provision.checked ? t("Installera / uppdatera scheman med tillfälliga installationskonton") : t("Schemainstallation är inte vald."), "hint"));
            }
            review.append(card);
        });
    }
    const stepLabels = [t("1 · Webbplats"), t("2 · Databaser"), t("3 · Identitet"), t("4 · Certifikat"), t("5 · Kontrollera och spara")];
    $("steps").replaceChildren(...stepLabels.map((label, index) => {
        const item = node("li"), button = node("button", label); button.type = "button";
        button.setAttribute("aria-controls", "step-" + index); button.addEventListener("click", () => goTo(index)); item.append(button); return item;
    }));
    function renderChecks(items, target = results) {
        target.replaceChildren();
        items.forEach(check => {
            const row = node("div", undefined, "setup-check " + (check.passed ? "passed" : "failed"));
            const label = node("strong");
            label.append(node("span", check.passed ? t("Godkänd: ") : t("Åtgärda: ")), node("span", t(check.nameKey ?? check.name)));
            row.append(label, node("p", t(check.messageKey ?? check.message)));
            if (!check.passed && check.details) row.append(node("pre", check.details, "setup-error-details"));
            if (!check.passed && target === results) {
                const name = check.nameKey ?? check.name;
                const index = name === "Active Directory" ? 2 : ["Krypteringscertifikat", "Auditcertifikat", "Integritetscertifikat"].includes(name) ? 3 : ["Valvdatabas", "Audit: INSERT utan ändringsrätt", "Audit: SELECT utan ändringsrätt", "Databasscheman"].includes(name) ? 1 : null;
                if (index !== null) row.append(editButton(index));
            }
            target.append(row);
        });
    }
    validate.addEventListener("click", async () => {
        const invalid = Object.values(fields).find(input => !input.checkValidity());
        if (invalid) { step = sections.findIndex(s => s.contains(invalid)); show(); invalid.reportValidity(); return; }
        const testedRevision = revision; passed = false;
        validate.disabled = finish.disabled = true; results.setAttribute("aria-busy", "true"); notice(fields.provision.checked ? t("Förbereder scheman och testar säkerhetsinställningar…") : t("Testar anslutningar och säkerhetsinställningar…"));
        try { const body = request(); await saveDraft(); const result = await post("validate", body); if (testedRevision !== revision) return; renderChecks(result.checks);
            passed = result.passed; finish.disabled = !passed; if (passed) { fields.provision.checked = false; adminFields.hidden = true; fields.vaultAdminPassword.value = fields.auditAdminPassword.value = ""; } notice(passed ? t("Alla kontroller passerade. Spara konfigurationen för att slutföra.") : t("Åtgärda de markerade kontrollerna och försök igen."), !passed);
            renderReview();
        } catch (error) { if (testedRevision === revision) { if (error.checks) renderChecks(error.checks); notice(error.message, true); } } finally { validate.disabled = false; results.setAttribute("aria-busy", "false"); }
    });
    finish.addEventListener("click", async () => {
        if (!passed) return; finish.disabled = validate.disabled = true;
        try { await post("finish"); Object.values(fields).forEach(input => { if (input.type === "password") input.value = ""; });
            $("steps").replaceChildren(); $("setup-form").replaceChildren(); const complete = node("div", undefined, "setup-complete"); const heading = node("h2", t("Installationen är sparad")); heading.tabIndex = -1; complete.append(heading, node("p", t("Starta om Valvras IIS-applikationspool. Öppna sedan webbplatsen igen för att använda valvet."))); const link = node("a", t("Öppna Valvra"), "button primary"); link.href = "/"; complete.append(link); $("setup-form").append(complete); heading.focus(); notice(t("Engångskoden har förbrukats. Konfigurationen är krypterad på servern."));
        } catch (error) {
            if (error.checks) { renderChecks(error.checks); passed = false; }
            notice(error.message, true); finish.disabled = !passed; validate.disabled = false;
        }
    });
    $("next").addEventListener("click", () => { const invalid = Array.from(sections[step].querySelectorAll("input,select")).find(input => !input.checkValidity()); if (invalid) { invalid.reportValidity(); return; } step = Math.min(step + 1, sections.length - 1); show(true); }); $("previous").addEventListener("click", () => { step = Math.max(0, step - 1); show(true); });
    $("setup-form").addEventListener("invalid", event => { event.target.setAttribute("aria-invalid", "true"); notice(t("Kontrollera fältet ”{field}”. {reason}", {field: event.target.labels[0].textContent, reason: i18n.validation(event.target)}), true); }, true);
    $("setup-form").addEventListener("submit", event => event.preventDefault());
    const invalidate = () => { revision++; passed = false; finish.disabled = true; results.replaceChildren(); $("setup-notice").hidden = true; };
    $("setup-form").addEventListener("input", invalidate); $("setup-form").addEventListener("change", invalidate);
    const draftActions = node("div", undefined, "actions");
    const save = node("button", t("Spara utkast"), "button"); save.type = "button";
    const restore = node("button", t("Läs in sparat utkast"), "button"); restore.type = "button";
    draftActions.append(save, restore);
    const draftPanel = node("div", undefined, "panel-body");
    draftPanel.append(draftActions, node("p", t("Spara när som helst och fortsätt senare med installationskoden. Utkast sparas även före tester. Inga lösenord eller installationskoder sparas i utkast."), "hint"));
    $("setup-form").insertBefore(draftPanel, $("setup-fields"));
    async function draftAction(action) {
        if (value("code").length !== 64) { step = 0; show(true); fields.code.focus(); notice(t("Ange installationskoden med 64 tecken för att spara eller läsa in ett utkast."), true); return; }
        save.disabled = restore.disabled = true;
        try { await action(); } catch (error) { notice(error.message, true); }
        finally { save.disabled = restore.disabled = false; }
    }
    save.addEventListener("click", () => draftAction(async () => {
        await saveDraft(); notice(t("Utkastet är sparat krypterat på servern. Installationen är inte slutförd."));
    }));
    restore.addEventListener("click", () => draftAction(async () => {
        const loadedRevision = revision;
        const result = await post("draft/load", {});
        if (loadedRevision !== revision) return;
        if (!result.draft) { notice(t("Det finns inget sparat utkast.")); return; }
        const apply = () => {
            for (const [key, saved] of Object.entries(result.draft.fields)) {
                if (!fields[key] || excludedDraftFields.has(key) || fields[key].type === "password") continue;
                if (fields[key].type === "checkbox") fields[key].checked = saved === "true";
                else fields[key].value = saved;
            }
        };
        apply();
        // Recompute dependent visibility before restoring credential values.
        Object.values(fields).forEach(input => input.dispatchEvent(new Event("change", {bubbles: true})));
        apply(); Object.values(fields).forEach(input => { if (input.type === "password" && input !== fields.code) input.value = ""; });
        invalidate(); step = Math.max(0, Math.min(4, result.draft.step)); show(true);
        notice(t("Utkastet är inläst. Kör kontrollerna igen innan installationen slutförs."));
    }));
    document.addEventListener("valvra:languagechange", () => { if (step === sections.length - 1) renderReview(); });
    show();
    fetch("/api/setup/status", { credentials: "same-origin", cache: "no-store", headers: {"X-Valvra-Language": i18n.language} }).then(response => { if (!response.ok) throw new Error(t("Windows-inloggningen kunde inte verifieras.")); return response.json(); }).then(status => {
        csrf = status.csrfToken;
        if (!status.canConfigure) { $("setup-form").hidden = true; notice(status.installed ? t("Valvra är redan konfigurerat. För att ändra installationen, generera en ny engångskod på servern enligt README.") : t("Generera en installationskod på servern enligt README.")); }
    }).catch(error => notice(error.message, true));
})().catch(error => {
    const target = document.getElementById("setup-error"); target.hidden = false;
    target.textContent = error.message || "Could not load setup / Kunde inte läsa installationen.";
});
