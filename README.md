# Valvra

En open source-lösenordshanterare för organisationer, byggd med .NET. Lösenord och programlicenser hör till resurser i ett hierarkiskt träd. Åtkomst tilldelas AD-användare och grupper, med separat rätt att läsa, ändra och administrera. Tillfällig läsrätt och signerad audit ingår.

Gränssnittet och installationsguiden finns på **svenska och engelska**. Välj **Språk / Language** i sidhuvudet; valet sparas mellan besök. [Språkstöd och hur översättningar läggs till](docs/LOCALIZATION.md).

**Licens: EUPL-1.2.** Fri användning i offentlig sektor, företag och privat enligt [licensvillkoren](LICENSE). Projektet är under utveckling; använd inte verkliga hemligheter innan installation, återställning och säkerhetsgranskning har verifierats i er miljö.

## Installation – Windows och IIS

### 1. Förbered servern

- En domänansluten Windows Server med IIS, **Windows Authentication** och en uppdaterad [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0).
- Ett DNS-namn och ett betrott HTTPS-certifikat för webbplatsen.
- MSSQL eller PostgreSQL med betrott TLS-certifikat. Skapa två tomma databaser: **Valvra** och **ValvraAudit**.
- Ett Windows-tjänstekonto, gärna **dMSA** i en förberedd Windows Server 2025/AD-miljö eller gMSA, med katalogläsrätt i AD. För MSSQL använder valvet och båda auditkopplingarna samma konto via **Integrated Security**, utan kontolösenord i Valvra. Se [miljökrav och databasroller](docs/DATABASES.md).
- Ett aktivt personkonto för installatören. Kontot som slutför installationen med serverns engångskod får behörighets- och systemadministration; globala rättigheter tilldelas sedan endast personkonton.

Audit körs **i samma tjänst, i separat databas**. Ge tjänstekontot `valvra_audit_runtime` i auditdatabasen: endast `SELECT` och `INSERT`, utan rätt att ändra, radera eller administrera audit. Ett separat läskonto är valfritt. Installationskonton används separat för schemaändringar. Se [auditguiden och SQL-skripten](docs/AUDIT.md).

För Kerberos SSO med en egen IIS-tjänsteidentitet ska AD-administratören registrera webbplatsens HTTP-SPN på tjänstekontot, exempelvis `setspn -S HTTP/valvra.example.se EXAMPLE\svcValvra$`. Kontrollera att SPN inte finns på ett annat konto. Klienternas webbläsarpolicy behöver tillåta automatisk Windows-inloggning till webbplatsens DNS-namn.

### 2. Publicera och installera

Hämta `Valvra-1.0.0-win-x64.zip` från [release 1.0.0](https://github.com/twids/Valvra/releases/tag/v1.0.0) och packa upp på servern. Paketet innehåller applikationen, `Install-Iis.ps1`, SQL-skript och dokumentation. Kontrollera filens SHA-256 mot releasens `SHA256SUMS.txt`. Version 1.0.0 är avsedd för nyinstallation med tomt valv; konvertering av tidigare preview-data ingår inte. Se [versionshistoriken](CHANGELOG.md).

För att i stället bygga från källkod, använd taggen `v1.0.0` och .NET 10 SDK från repots rot:

```powershell
dotnet restore
dotnet test
dotnet publish src/Valvra.Web -c Release -r win-x64 --self-contained false -o artifacts/publish
```

Kopiera publiceringsmappen och `deploy/Install-Iis.ps1` till servern. Kör följande i **Windows PowerShell som administratör**. Ersätt värdena med era egna:

```powershell
.\Install-Iis.ps1 -PublishedPath C:\Temp\ValvraPublish `
  -HostName valvra.example.se -HttpsCertificateThumbprint "ERT-HTTPS-TUMAVTRYCK" `
  -ServiceAccount 'EXAMPLE\svcValvra$'
```

Skriptet installerar en ny IIS-webbplats, sätter filbehörigheter, skapar tre separata kryptografiska certifikat för kryptering, audit och valvintegritet och visar **installationskod samt certifikatens tumavtryck**. Spara certifikatbackuper säkert enligt [driftguiden](docs/OPERATIONS.md). Skriptet är avsett för en ny installation och skriver inte över en befintlig webbplats.

### 2.1. Kräv skydd för Windows-inloggningen

**Obligatoriskt före setup och produktionsdrift:** nuvarande `Install-Iis.ps1` aktiverar Windows Authentication men sätter inte Extended Protection. Konfigurera detta manuellt. HTTPS ensamt förhindrar inte att Windows-autentisering vidarebefordras av en angripare.

För den dokumenterade installationen med HTTPS direkt till IIS:

1. Öppna **IIS Manager → Sites → Valvra → Authentication**; använd ert webbplatsnamn om det är ett annat.
2. Kontrollera **Anonymous Authentication: Disabled** och **Windows Authentication: Enabled**.
3. Öppna **Windows Authentication → Advanced Settings** och sätt **Extended Protection: Required**. Läs tillbaka inställningen efter att den sparats. `Accept`/`Allow` är otillräckligt för detta krav.
4. Verifiera med testkonto att SSO fungerar och att autentisering utan giltig channel binding nekas. Dokumentera resultatet före drift; den gröna setup-guiden verifierar inte denna IIS-inställning.

Använd Kerberos med rätt HTTP-SPN enligt steg 1 och verifiera faktiskt använt protokoll med AD/IIS-administratören. `Negotiate` kan falla tillbaka till NTLM. Eventuellt tillåten NTLM kräver också verifierat relayskydd. Se [Windows Authentication Providers](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/authentication/windowsauthentication/providers/).

Reverse proxy eller TLS-terminering framför IIS kräver separat validering av channel/service binding för hela anslutningen. Följ [Microsofts Extended Protection-anvisningar](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/authentication/windowsauthentication/extendedprotection/). Sänk inte skyddet till `Allow` eller `None` för att få en sådan installation att fungera.

### 3. Följ installationsguiden i webbläsaren

Öppna `https://valvra.example.se/setup` från en domänansluten klient med Windows SSO:

1. Ange engångskoden och webbplatsens DNS-namn.
2. Ange valvdatabas och auditdatabas. Välj **Windows / Integrated Security** för båda när MSSQL används. Samma tjänstekonto används för auditläsning och tillägg; lämna **Använd separat auditläsare** avmarkerat för dMSA/gMSA utan extra kontolösenord.
3. Ange LDAPS-server, katalogens uppslagsbas och de tre certifikatens tumavtryck. Ange vid behov separata sökbaser för personkonton och säkerhetsgrupper samt en godkänd LDAP-testserver.
4. För en ny databas: välj att installera scheman med **tillfälliga installationskonton**. Dessa konton sparas inte. Tilldela sedan runtime-kontona de dokumenterade databasrollerna och testa igen utan schemainstallation.
5. Klicka **Testa anslutningar och rättigheter**. När alla kontroller passerar: **Spara och slutför installationen**.

Starta om IIS-applikationspoolen `Valvra` och öppna webbplatsen igen. Engångskoden är då förbrukad. Konfigurationen lagras DPAPI-krypterad i `App_Data`, med NTFS-behörigheter begränsade till tjänsten och serveradministratörer.

Denna version kräver ett **tomt valv vid nyinstallation**. Ingen konvertering av tidigare preview-data ingår. Setup skapar en signerad integritetskontrollpunkt i `App_Data`, utanför databasen. Ge aldrig databasadministrationens identiteter åtkomst till dessa filer eller tjänstens privatnycklar. Saknad kontrollpunkt får inte ersättas genom att godkänna innehållet i en befintlig databas. Läs och provkör [backup och återställning](docs/OPERATIONS.md) före drift; kontrollpunkten är maskinbunden.

### 4. Skapa ert första valv

Skapa en resursgrupp, skapa en resurs och tilldela en AD-grupp läs- eller ändringsrätt. Lägg sedan till lösenord eller licenser. Behörighetsadministratörer får inte automatiskt läsa hemligheter; även deras läsrätt tilldelas uttryckligen.

Öppna **Inställningar** för vanlig administration: ändra katalogserver och sökbaser, testa anslutningen eller tilldela personkonton globala rättigheter. **Spara inställningar** verifierar anslutningen och ditt konto före sparande; katalogändringar gäller för nya förfrågningar utan omstart eller engångskod. Databaser och certifikat ändras fortfarande via operatörens installationsflöde. Se [inställningar, roller och identitetsmoduler](docs/SETTINGS.md).

Öppna ett gruppnamn i **Resursgrupper** för att se dess resurser och navigera till undergrupper. Resurser i undergrupper ingår som standard; avmarkera **Inkludera undergrupper** för att bara se gruppens egna resurser. Sökning och filter bevaras när du öppnar en resurs och går tillbaka. Brödsmulorna visar vägen genom de grupper du har åtkomst till. Gruppens administrationsåtgärder finns under **Hantera grupp**.

Alla huvudvyer, resursgrupper och resurser har egna adresser. Musens och webbläsarens **Bakåt/Framåt** fungerar; kopiera adressen eller använd länkens meny för att dela den med en kollega. Kollegans egna behörigheter gäller. Direktlänkar och omladdning fungerar även i demon. Se [navigering, länkar och historik](docs/NAVIGATION.md).

## Drift och säkerhet

- [Audit, läsning och tillägg utan ändringsrätt](docs/AUDIT.md)
- [Databaser, Windows Auth och rättigheter](docs/DATABASES.md)
- [Backup, återställning, certifikatrotation och omkonfiguration](docs/OPERATIONS.md)
- [Säkerhetsmodell och begränsningar](docs/SECURITY-MODEL.md)
- [Verifierade tester och kontroll vid installation](docs/VERIFICATION.md)
- [Grundläggande tillgänglighet och WCAG-avgränsning](docs/ACCESSIBILITY.md)
- [Rapportera sårbarheter privat](SECURITY.md)

Hemligheter krypteras med AES-256-GCM och separata datanycklar, skyddade med ett RSA-certifikat utanför databasen. Krypteringen binder värdet till installation, resurs, post och version. En separat signerad kontrollpunkt skyddar även resursstruktur, rättigheter och audit-outbox mot ändringar direkt i databasen. Audit signeras innan den sparas i outboxen. Om integritet eller audit inte kan verifieras lämnas inga hemligheter ut.

Vid licensredigering behålls befintlig nyckel som standard. **Ersätt** kräver en ny nyckel; **Töm** är ett uttryckligt val. Krypterad versionshistorik gör att tidigare nycklar kan återställas av en användare med både läs- och ändringsrätt. Fokusförlust och fem minuters inaktivitet rensar känsliga fält, men behåller vanliga uppgifter i öppna redigeringsformulär. Utkastet finns endast i sidans minne och försvinner vid omladdning, avbrytande eller när fliken stängs. Visade hemligheter stängs efter 30 sekunder eller vid fokusförlust. Ett redan kopierat värde kan inte återkallas.

## Utveckling och bidrag

En separat, skrivskyddad demo med fasta exempeldata finns i [demoguiden](docs/DEMO.md). Den kan köras på Linux utan AD eller databaser och ingår inte i produktionsappen.

MSSQL och PostgreSQL har separata EF Core-migrationer. Övriga databaser kräver en ny provider och verifiering. Inloggning, katalog, nyckelskydd, credential-test och audit har separata gränssnitt. Första versionens produktionsprovider är AD/Windows SSO; lokala konton och OIDC är inte implementerade.

```powershell
dotnet tool restore
dotnet restore
dotnet test
```

HTTP-testerna använder testidentiteter i testprojektet. Ingen simulerad inloggning finns i produktionsapplikationen. Läs [CONTRIBUTING.md](CONTRIBUTING.md) innan du skickar ändringar.

Kör även klientens säkerhetstester med Node.js: `node --test tests/browser/*.test.cjs`. För regressioner vid SDK-/ramverksuppgradering, se [verifieringsguiden](docs/VERIFICATION.md).

Gränssnittet har en grundläggande tillgänglighetsbas enligt relevanta WCAG 2.2 A/AA-kriterier. Kör `npm ci`, `npx playwright install chromium` och `npm run test:accessibility` för automatiska tillgänglighets- och tangentbordstester. Se [tillgänglighetsguiden](docs/ACCESSIBILITY.md) för omfattning, manuella releasekontroller och begränsningen med tidsbegränsad visning av hemligheter.
