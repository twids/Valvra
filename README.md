# Valvra

En open source-lösenordshanterare för organisationer, byggd med .NET. Lösenord och programlicenser hör till resurser i ett hierarkiskt träd. Åtkomst tilldelas AD-användare och grupper, med separat rätt att läsa, ändra och administrera. Tillfällig läsrätt och signerad audit ingår.

**Licens: EUPL-1.2.** Fri användning i offentlig sektor, företag och privat enligt [licensvillkoren](LICENSE). Projektet är under utveckling; använd inte verkliga hemligheter innan installation, återställning och säkerhetsgranskning har verifierats i er miljö.

## Installation – Windows och IIS

### 1. Förbered servern

- En domänansluten Windows Server med IIS, **Windows Authentication** och en uppdaterad [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0).
- Ett DNS-namn och ett betrott HTTPS-certifikat för webbplatsen.
- MSSQL eller PostgreSQL med betrott TLS-certifikat. Skapa två tomma databaser: **Valvra** och **ValvraAudit**.
- En Windows-tjänsteidentitet, helst gMSA, med katalogläsrätt i AD. För MSSQL använder valvet och auditskrivaren denna identitet via **Integrated Security**.
- Två AD-grupper: behörighetsadministratörer och auditläsare. Installatörens konto ska ingå i administratörsgruppen. Anteckna gruppernas SID.
- En separat databasidentitet för auditläsning. För Windows-inloggning används ett separat läskonto; dess inloggningsmaterial sparas krypterat på servern.

Audit körs **i samma tjänst, i separat databas**. Auditskrivaren ska endast få `INSERT`; auditläsaren endast `SELECT`. Installationskonton används separat för schemaändringar. Se [auditguiden och SQL-skripten](docs/AUDIT.md).

För Kerberos SSO med en egen IIS-tjänsteidentitet ska AD-administratören registrera webbplatsens HTTP-SPN på tjänstekontot, exempelvis `setspn -S HTTP/valvra.example.se EXAMPLE\svcValvra$`. Kontrollera att SPN inte finns på ett annat konto. Klienternas webbläsarpolicy behöver tillåta automatisk Windows-inloggning till webbplatsens DNS-namn.

### 2. Publicera och installera

Hämta det färdiga Windows-paketet från [v0.1.0-preview.1](https://github.com/twids/Valvra/releases/tag/v0.1.0-preview.1) och packa upp det på servern. Paketet innehåller `Install-Iis.ps1` och dokumentationen. Då behövs inget SDK på installationsservern, endast Hosting Bundle.

För att bygga paketet själv:

Från repots rot, med .NET 10 SDK:

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

Skriptet installerar en ny IIS-webbplats, sätter filbehörigheter, skapar två separata kryptografiska certifikat och visar **installationskod samt certifikatens tumavtryck**. Spara certifikatbackuper säkert enligt [driftguiden](docs/OPERATIONS.md). Skriptet är avsett för en ny installation och skriver inte över en befintlig webbplats.

### 3. Följ installationsguiden i webbläsaren

Öppna `https://valvra.example.se/setup` från en domänansluten klient med Windows SSO:

1. Ange engångskoden och webbplatsens DNS-namn.
2. Ange valvdatabas och de två auditanslutningarna. Välj **Windows / Integrated Security** för valv och auditskrivare när MSSQL används. Auditläsaren måste ha egen identitet.
3. Ange LDAPS-server, sökbas, AD-gruppernas SID och de två certifikatens tumavtryck. Lägg vid behov till en godkänd LDAP-testserver.
4. För en ny databas: välj att installera scheman med **tillfälliga installationskonton**. Dessa konton sparas inte. Tilldela sedan runtime-kontona de dokumenterade databasrollerna och testa igen utan schemainstallation.
5. Klicka **Testa anslutningar och rättigheter**. När alla kontroller passerar: **Spara och slutför installationen**.

Starta om IIS-applikationspoolen `Valvra` och öppna webbplatsen igen. Engångskoden är då förbrukad. Konfigurationen lagras DPAPI-krypterad i `App_Data`, med NTFS-behörigheter begränsade till tjänsten och serveradministratörer.

### 4. Skapa ert första valv

Skapa en resursgrupp, skapa en resurs och tilldela en AD-grupp läs- eller ändringsrätt. Lägg sedan till lösenord eller licenser. Behörighetsadministratörer får inte automatiskt läsa hemligheter; även deras läsrätt tilldelas uttryckligen.

## Drift och säkerhet

- [Audit, INSERT-only och separata identiteter](docs/AUDIT.md)
- [Databaser, Windows Auth och rättigheter](docs/DATABASES.md)
- [Backup, återställning, certifikatrotation och omkonfiguration](docs/OPERATIONS.md)
- [Säkerhetsmodell och begränsningar](docs/SECURITY-MODEL.md)
- [Verifierade tester och kontroll vid installation](docs/VERIFICATION.md)
- [Rapportera sårbarheter privat](SECURITY.md)

Hemligheter krypteras med AES-256-GCM och separata datanycklar, skyddade med ett RSA-certifikat utanför databasen. Audit signeras med ett annat certifikat. Om audit inte kan skrivas lämnas inga hemligheter ut. Ett lösenord som redan kopierats kan inte återkallas genom att en tillfällig rätt upphör.

## Utveckling och bidrag

MSSQL och PostgreSQL har separata EF Core-migrationer. Övriga databaser kräver en ny provider och verifiering. Inloggning, katalog, nyckelskydd, credential-test och audit har separata gränssnitt. Första versionens produktionsprovider är AD/Windows SSO; lokala konton och OIDC är inte implementerade.

```powershell
dotnet tool restore
dotnet restore
dotnet test
```

HTTP-testerna använder testidentiteter i testprojektet. Ingen simulerad inloggning finns i produktionsapplikationen. Läs [CONTRIBUTING.md](CONTRIBUTING.md) innan du skickar ändringar.
