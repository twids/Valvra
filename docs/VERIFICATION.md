# Verifiering och uppgraderingsregressioner

## Automatiskt verifierat

Lokalt verifierat 2026-10-05: **112 .NET-tester i Release och 10 klienttester passerade**. Körningen inkluderade två kontrakttester mot verklig SQL Server 2022 och PostgreSQL 17 i isolerade, disponibla Dockhand-containrar, samt Windows DPAPI-tester. Vid lokal körning utan testdatabaser hoppas endast de två databastesterna över.

CI-konfigurationen kör båda databasleverantörerna på Linux och övriga tester på Windows, samlar Cobertura/TRX och publicerar IIS-paketet. Dessa nya CI-steg är konfigurerade men har ännu inte körts på GitHub för denna opublicerade ändring.

Testerna omfattar bland annat:

- AES-GCM, manipulering av ciphertext/nonce/tag/datanyckel, bindning till installation/resurs/post/version och byte av nyckelskydd.
- Direkt SQL-manipulation av rättigheter, ägare, resursstruktur, lösenord, licenser och outbox: nekad åtkomst före dekryptering, signering eller leverans.
- Raderad/ändrad kontrollpunkt, databasrollback efter omstart, oväntade triggerändringar och avbrott före/efter checkpoint/commit, inklusive begränsad operatörsåterhämtning.
- Ärvda rättigheter, administratör utan automatisk läsrätt och exakt utgång av tillfällig åtkomst.
- Auditavbrott före utlämning/ändring, återleverans efter commit och förlorad commit-kvittens.
- Båda databasernas migrationer och faktiska SELECT+INSERT-rättigheter utan UPDATE/DELETE/TRUNCATE/ALTER för gemensamt auditkonto, samt valfria INSERT-only/SELECT-only-identiteter.
- HTTP-avvisning, autentisering, CSRF, säkerhetsheaders och försök att läsa en annan användares post via ID.
- Installationskod, DPAPI-skydd och anslutningssträngar för tjänstens Windows-identitet.
- Separat rätt för LDAP-test, ett testanrop, tidsbegränsning och behörighetskontroll efter långsam audit.
- Versionshistorik, konfliktskydd, soft delete och licensplatser utan dekryptering av licensnyckel.
- Licensmetadata utan läsrätt, uttryckliga behåll/ersätt/töm-val, krypterad licenshistorik och återställning med både läs- och ändringsrätt.
- Gruppdjup 128/129 vid skapande och flytt, borttagna ägare, återkallade rättigheter och ändrat arv efter resursflytt.
- Klientens fokusförlust, inaktivitet och sena utlämningssvar: känsliga fält rensas, metadatautkast behålls och tom ersättningsnyckel kan inte sparas.
- Den separata demon visar endast fasta syntetiska data och avvisar ändring, setup och credential-input. Dess assembly refererar inte till valv-/databasbibliotek.

Resursvyn, lösenordsvisning, historik, behörighetsformulär, licensöversikt, audit och installationsguiden har även kontrollerats i webbläsare med syntetiska data. Testserver och testidentiteter ingår endast i testprojekten och publiceras inte i IIS-paketet.

Licensutkastets rensning har kontrollerats i den riktiga sidan med simulerad fokusförlust. Den körande demon i Dockhand gav HTTP 200 för sidan/klientscriptet, 405 för mutation, 400 för credential-input och 404 för setup. Denna demo är separat från produktionen och använder inga databaser.

## Kodtäckning och .NET-uppgradering

Rapporterna omfattar produktionsmodulerna Core, Infrastructure och Web; tester, demo och genererade migrationer räknas inte med. Kritiska källfiler har följande verifierade radtäckning (asynkrona representationer av samma källrad räknas en gång):

| Källfil | Radtäckning |
| --- | ---: |
| EnvelopeCipher | 97,8 % |
| VaultIntegrity | 94,0 % |
| AccessService | 100,0 % |
| AuditService | 95,1 % |
| VaultOperations | 95,8 % |
| Skyddad VaultService-fasad | 93,9 % |

Databasjobbet stoppar om någon av dessa filer faller under 90 %. Hela Infrastructure ligger omkring 70 % och Web omkring 48 %; tabellen beskriver de kritiska filerna, inte full täckning av Windows/IIS/LDAP/certifikatintegration. Radtäckning ersätter inte meningsfulla negativa tester.

Vid uppgradering: ändra `global.json`, målramverk och paketversioner i en separat gren, återställ och kör:

```powershell
dotnet test Valvra.slnx -c Release --settings tests/coverage.runsettings --collect:"Code Coverage" --logger "trx;LogFileName=tests.trx" --results-directory artifacts/test-results
node --test tests/browser/*.test.cjs
.\tests\Assert-SecurityCoverage.ps1
dotnet publish src/Valvra.Web -c Release -r win-x64 --self-contained false -o artifacts/publish
```

Sätt `VALVRA_TEST_SQLSERVER` och `VALVRA_TEST_POSTGRESQL` till **dedikerade disponibla testservrar** för båda databaskontrakten, eller använd CI:s isolerade databaser. Testerna skapar och tar bort egna slumpnamngivna databaser och konton. Produktionsservrar får inte användas.

Frysta AES-GCM-, JSON- och hashvektorer skyddar befintligt format 2 mot oavsiktliga serializer-/kultur-/SDK-ändringar. Modellinventering och migrationskontroller omfattar båda providers. Ändra inte de frysta förväntningarna för att få en uppgradering grön utan att utreda varför lagringskontraktet ändrades. Provåterställ dessutom en backup av nuvarande format och genomför miljökontrollerna nedan med det nya runtime-paketet. En framtida .NET-version är inte verifierad förrän denna körning och Windows/IIS/AD-kontrollerna passerar.

CI använder lösenordsautentisering och undantag från TLS-verifiering **enbart i dess tillfälliga databaser**. Produktionskonfigurationen kräver betrodda certifikat. CI bevisar därför inte en viss organisations Kerberos-, LDAPS- eller certifikatkonfiguration.

## Kontroller vid installation i den egna miljön

En verklig domänansluten IIS/AD-installation har inte varit tillgänglig i utvecklingsmiljön. Följ README och låt setup-guiden verifiera tjänsteidentitetens databasanslutningar, katalog, certifikat och auditrollens rättigheter. Verifiera dMSA/gMSA separat i den förberedda Windows/AD-miljön. Kontrollera därefter med testkonton och syntetiska hemligheter:

1. Windows SSO från klient, aktuell AD-grupptillhörighet och avvisning av inaktiverat konto. Genomför och dokumentera även [README:s obligatoriska verifiering av Windows-inloggningen](../README.md#21-kräv-skydd-för-windows-inloggningen); en lyckad setup ersätter inte den kontrollen.
2. En läsare, en användare med ändringsrätt och en användare utan tilldelning; kontrollera både gränssnitt och direkta API-anrop.
3. Tillfällig åtkomst före och efter utgångstid, samt signerad audit för läsning och ändring.
4. En godkänd LDAP-testprofil med ett särskilt testkonto och organisationens kontolåsningspolicy.
5. Backup och faktisk återställning av båda databaserna och kryptografiska nycklar på en isolerad server.

Denna kontroll behövs för varje installationsmiljö; automatiska tester ersätter inte en oberoende säkerhetsgranskning inför hantering av verkliga organisationshemligheter.

## Resurs- och gruppfilter i audit

Auditfiltrens regressioner omfattar historisk grupptillhörighet vid resurs-/gruppflytt, namnbyte, skapande, borttagning och misslyckade ändringar; signerad metadata utan hemliga värden; filtrering med/utan undergrupper; identiska filter i API/lista/export samt nekad åtkomst utan auditläsarroll. `AuditScopeTests` innehåller 17 nya C#-fall. De två provider-kontraktstesterna har kompletterats med riktiga SQL-frågor för både filtersökning och historiska vallistor. Webbläsartesterna finns i `tests/accessibility/audit-filters.spec.cjs` och ingår i det vanliga tillgänglighetsjobbet.

Verifierat lokalt 2026-10-05: 127 C#-tester, 10 klientsäkerhetstester och de två nya Playwright/axe-testerna passerade. Säkerhetens befintliga 90%-gränser passerade, inklusive den nya kontrollen för `AuditScopeCapture.cs` (36/36 täckta rader). De två kontraktstesterna för MSSQL/PostgreSQL hoppades över eftersom testanslutningar saknas; deras nya SQL-frågor är därför inte verifierade mot riktiga databaser i denna körning. Den lokala SQLite-baserade testvärden verifierar inte de providerspecifika JSON-frågorna eller deras prestanda. Kör provider-kontrakten med separata testdatabaser före produktionsdrift.

För separat verifiering utan att skriva över en körande förhandsvisnings byggutdata:

```powershell
dotnet build tests/Valvra.Preview -c Release -o artifacts/audit-filter-preview
npx playwright test --config tests/browser/audit-playwright.config.cjs
```

## Gemensamt tjänstekonto för audit

Lokal verifiering 2026-10-05: 151 C#-tester passerade; två kontraktstester mot riktiga MSSQL/PostgreSQL-servrar skippades eftersom testanslutningar saknas. 24 nya setupfall täcker gemensam Integrated Security utan extra lösenord, valbart läskonto, bibehållen databasseparation, tillåtna minimirättigheter samt nekade destruktiva och okända behörigheter. Två isolerade Playwright/axe-tester passerade för standardinstallation och den valfria läskopplingen, inklusive rensning av dess lösenord när den stängs av.

Databaskontrakten innehåller även kontroll av verklig SELECT+INSERT, nekad UPDATE/DELETE/TRUNCATE/DROP och setupguidens behörighetsfrågor för det gemensamma kontot. Dessa providerfall och faktisk dMSA-inloggning har inte körts i denna verifiering; kontrollera dem före produktionsdrift. Testade policyresultat ersätter inte verklig SQL-behörighetsverifiering.

```powershell
dotnet test tests/Valvra.Tests -c Release -o artifacts/shared-audit-tests
dotnet build tests/Valvra.Preview -c Release -o artifacts/shared-audit-preview
npx playwright test --config tests/browser/setup-playwright.config.cjs
```

## Tillgänglighetsregressioner

Grundläggande WCAG-kontroller och tangentbordstester körs separat med `npm ci`, `npx playwright install chromium` och `npm run test:accessibility`. Se [tillgänglighetsguiden](ACCESSIBILITY.md) för kriterier, omfattning och manuella releasekontroller.

Lokal verifiering 2026-10-05: 13 Playwright/axe-tester och 10 klientsäkerhetstester passerade. Dessutom passerade 110 C#-tester med `DatabaseContractTests` undantagna; de två kontraktstesterna mot riktiga MSSQL/PostgreSQL-databaser kördes inte om för denna gränssnittsändring. Tillgänglighetsjobbet är tillagt i GitHub Actions men har inte körts där från denna lokala arbetskopia. Detta bekräftar utvalda gränssnittskontroller, inte full WCAG-överensstämmelse eller manuell NVDA-verifiering.

## Språkregressioner

Svenska och engelska delar språkfiler mellan klient och server. Se [språkguiden](LOCALIZATION.md). Lokal verifiering av språkstödet 2026-10-05: **168 C#-tester**, **17 Node-tester** och **24 Playwright/axe-tester** passerade. Databaskontraktstesterna mot riktiga MSSQL/PostgreSQL-databaser ingick inte i denna körning.

Språktesterna kontrollerar katalogtäckning, formatparametrar, språkprioritet och återgång vid felaktiga headers, beständigt språkval, HTML-språk, formulärvalidering, installationsutkast, godkända installationskontroller, originalvärden i användardata samt oförändrade CSRF- och behörighetsgränser. Engelsk layout provas även vid 320 pixlars bredd och 200 % textstorlek. Båda språken kräver fortfarande manuell skärmläsarprovning inför release. Endast den isolerade preview-värden undantar anropsbegränsning för att sviten kan göra många anrop med samma syntetiska identitet; produktionsgränserna är kvar.

## Härdning av auditläsning efter säkerhetsgranskning

Verifierat lokalt 2026-10-05: **169 C#-tester**, **20 Node-tester** och **4 Playwright-tester för auditfiltren** passerade. Befintliga 90%-gränser för säkerhetskritiska filer passerade. Byggning gjordes med redan återställda paket och separat `--artifacts-path artifacts/audit-security-fix`; testloggar, TRX och Cobertura finns där.

`AuditReaderSecurityTests` använder den riktiga verifieraren och läs-/urvalslogiken med syntetiska datarader. Regressionerna omfattar ogiltiga representanter, manipulerade tidsprojektioner, trasig JSON, null/objekt som gruppkedja, för stora payloads, läsgränser, avbruten läsning, nekad auditåtkomst och filtrering/sidindelning efter verifiering. Klient- och webbläsartester kontrollerar att ogiltig kontext kan visas säkert och att fel i vallistan inte blockerar auditlistan. Klienttestet skiljer också identiska filterval genom deras exakta ID.

Båda provider-kontrakten har utökats med verklig INSERT av korrupta auditrader och osignerade tidsprojektioner. **De två testerna hoppades över** eftersom dedikerade testanslutningar saknas. Faktisk `GetChars`-hantering, SQL-läsning och prestanda på MSSQL/PostgreSQL är därför fortfarande inte verifierade för rättelsen; en testadapter ersätter inte provider-körning. AuditDatabase.cs hade 97/180 täckta källrader i denna körning. Kör båda kontrakten och belastningsprova representativ historik före produktionsdrift. Läsgränser och korruptionsmarkeringar dokumenteras i AUDIT.md.

## Navigering i resursgrupper

Lokal verifiering 2026-10-05: **169 C#-tester**, **24 Node-tester** och hela sviten med **35 Playwright/axe-tester** passerade. C#-körningen undantog de två `DatabaseContractTests`; riktiga MSSQL/PostgreSQL-databaser ingick inte. Gruppnavigeringen ändrar klienten och använder befintliga behörighetsfiltrerade API-svar.

`tests/browser/group-navigation.test.cjs` kontrollerar gruppernas ID-baserade omfattning, direkta resurser och undergrupper, sökning, identiska gruppnamn, saknade föräldrar, cykler och 128 nivåers hierarki. De sju fallen i `tests/accessibility/group-navigation.spec.cjs` provar en navigerbar hierarki, barn och barnbarn, brödsmulor, tangentbord, fokus, bevarade filter, svenska/engelska, förvalda grupper vid skapande, sena metadatasvar och layout vid 320 pixlars bredd. Automatiska axe-kontroller ingår; detta ersätter inte den manuella skärmläsarprovningen.

Gruppvyn och dess antal bygger enbart på metadata som servern har lämnat ut till den aktuella användaren. Saknade föräldrar hämtas inte separat och gruppvyn hämtar inga lösenord eller licensnycklar. Testernas undergrupper är syntetiska metadatafixturer; resursöppning använder den isolerade preview-värdens verkliga metadataendpoints. Behörighetskontroll och utlämning av hemligheter sker fortsatt på servern.

## URL:er och webbläsarhistorik

Verifierat lokalt 2026-10-05: **193 C#-tester**, **27 Node-tester** och hela sviten med **49 Playwright/axe-tester** passerade. De två `DatabaseContractTests` undantogs; verkliga MSSQL/PostgreSQL-databaser och domänansluten IIS/AD ingick inte. De befintliga 90%-gränserna för säkerhetskritisk kod passerade med rapporten i `artifacts/routing-coverage`; `VaultIntegrity.cs` hade 94,1% täckta källrader.

De tre kontraktstesterna i `tests/browser/navigation.test.cjs` provar vyer, stabila ID:n, ogiltiga adresser och osäkra routeparametrar. De tio fallen i `tests/accessibility/routing.spec.cjs` provar hela navigeringskedjan inklusive inställningar, Bakåt/Framåt, direktlänkar, omladdning, tidiga klick under identitetsverifiering, filter och auditsidor, vanlig öppning i ny flik, svenska/engelska UI-regressioner i den gemensamma sviten samt nekade länkar utan innehållsanrop. De verifierar att hemlighetsdialoger töms utan att återspelas och att sena svar inte ändrar en annan vy. Separata HTTP-tester kräver autentisering på alla UI-rutter och kontrollerar att API- och statiska filadresser inte skrivs om till HTML. Rutter och användarbeteende dokumenteras i [navigeringsguiden](NAVIGATION.md).

`IntegrityCancellationTests` reproducerade två fel före rättelsen: avbrott efter förberedelse av kontrollpunkt stoppade commit, och en avbruten auditpost lämnade en förberedd kontrollpunkt. Alla tre fall passerar efter rättelsen: avbrott före förberedelse rullar tillbaka, avbrott efter förberedelse slutför exakt den godkända ändringen, och signerad audit kan levereras vid nästa anrop. Befintliga tester för manipulation, rollback och uttrycklig operatörsåterhämtning passerade fortsatt. Den nya avbrottslogiken är verifierad med SQLite och syntetisk kontrollpunktslagring; providerspecifika nätverks-/commitfel kräver fortsatt test mot riktiga testdatabaser.

## Release 1.0.0

Verifierat lokalt 2026-10-06 med version 1.0.0: **194 C#-tester**, **27 Node-tester** och **49 Playwright/axe-tester** passerade. De två databasleverantörernas kontraktstester hoppades över lokalt eftersom testanslutningar saknas; de körs i GitHub Actions isolerade SQL Server-/PostgreSQL-miljö före publicering. Alla sju säkerhetskritiska filer passerade 90%-gränsen. Lokala TRX-/täckningsrapporter och webbläsarrapporter finns under `artifacts/release-1.0.0`.

Windows/IIS-paketet byggdes för `win-x64` med `--self-contained false`. Versionsnummer, installationsskript, SQL-skript och dokumentation kontrollerades, liksom att demo, testidentiteter, SQLite och skyddat installationsmaterial saknas. Webbläsartesternas förberedelse bygger nu både preview och demo, så att sviten fungerar från en ren arbetskopia. Den lokala körningen använde separata byggmappar för att behålla en redan körande förhandsvisning.

Domänansluten IIS/AD, Extended Protection, dMSA/gMSA, återställning i målmiljön och manuell skärmläsarprovning ingår inte i denna automatiska verifiering. Dessa avgränsningar gäller även release 1.0.0 enligt README och tillgänglighetsguiden.
