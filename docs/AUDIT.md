# Audit: samma tjänst, separat databas

Valvra skriver audit direkt från webbapplikationens .NET-process till en **separat databas**. Ingen separat auditserver, HTTP-mottagare eller bakgrundstjänst behöver installeras. Valvras ordinarie databas och auditdatabasen får ligga på samma databasserver. Ett gemensamt tjänstekonto kan användas, med olika rättigheter i respektive databas.

## Ett tjänstekonto, två databaser

| Anslutning | Användning | Runtime-behörighet |
| --- | --- | --- |
| Valvra | Valv, resurser, tilldelningar och transaktionell audit-outbox | Läs/skriv i Valvras tabeller; ingen schemaadministration |
| Audit writer | Lägga till signerade audithändelser | `INSERT` på audittabellen |
| Audit reader | Auditvyn, filtrering och export | `SELECT` på audittabellen |

Standardinstallationen använder **samma tjänsteidentitet och samma anslutningssträng** för båda auditkopplingarna. Ge tjänstekontot rollen `valvra_audit_runtime`: `SELECT` och `INSERT` på audittabellen, utan `UPDATE`, `DELETE`, `TRUNCATE`, `ALTER`, ägarskap eller möjlighet att ändra rättigheterna. Kontot får inte ha andra roller, kolumnrättigheter eller åtkomst till administrativa procedurer som kan ändra, radera eller ersätta audit. Audittransportens kod använder endast INSERT även när kontot också har läsrätt.

För MSSQL med Windows Integrated Security kan IIS köras som **ett dMSA- eller gMSA-konto**. Windows/AD hanterar kontots autentisering; Valvra behöver varken lagra tjänstekontots lösenord eller göra separat Windows-inloggning för auditläsning. Se [databasguiden](DATABASES.md) för installation och miljökrav. Konfigureras anslutningar manuellt används samma värde i `AuditDatabase:WriterConnectionString` och `AuditDatabase:ReaderConnectionString`; lämna `ReaderWindowsCredentials` tomt.

Separata identiteter är fortfarande valbara: `valvra_audit_writer` får endast INSERT och `valvra_audit_reader` endast SELECT. Aktivera **Använd separat auditläsare (valfritt)** i setup. Lägg inte ett gemensamt konto i dessa två MSSQL-roller: deras uttryckliga DENY-rättigheter blockerar läsning respektive tillägg. Använd i stället enbart `valvra_audit_runtime` i auditdatabasen.

Migrations-/installationsidentiteten får aldrig användas som runtime-identitet. Använd inte `db_owner`, `sysadmin`, tabell-/schemaägare eller PostgreSQL-superuser. Installationsguiden testar tillägg och läsning samt kontrollerar effektiva tabell- och kolumnrättigheter och vissa administrativa rättigheter. Okänt kontrollresultat stoppar installationen. DBA måste även granska bredare roller, rättighetsadministration och åtkomst via procedurer; setup är inte en fullständig kartläggning av alla privilegievägar.

Installationsskript: [MSSQL](../deploy/audit-sqlserver.sql) och [PostgreSQL](../deploy/audit-postgresql.sql). Kör dem som DBA i auditdatabasen. Bevara den definierade primärnyckeln och lägg inte till andra unika index utan att ändra återleveranshanteringen.

## Leverans och avbrott

Varje händelse får ett UUID, UTC-tid, aktörens stabila provider/ID, mål-ID, operation, resultat och korrelations-ID. Lösenord, licensnycklar, kontolösenord, hemliga anteckningar och kryptografiska nycklar får aldrig förekomma i audit.

För utlämning av en hemlighet måste auditdatabasen ha kvitterat den beständiga INSERT-operationen innan dekryptering och svar. `ReleaseAuthorized` betyder serverns godkännande av utlämning, inte bevis på att klienten faktiskt mottagit eller läst hemligheten.

För ändringar skrivs först en avsiktshändelse. Därefter genomförs förändringen och resultathändelsen i en lokal transaktion i Valvras databas. Resultatet levereras till auditdatabasen innan framgång rapporteras till klienten. Databaserna använder inte en distribuerad transaktion.

Om ändringen har committats och resultataudit sedan misslyckas kan ändringen redan vara genomförd trots felmeddelandet. Den beständiga outboxen återlevererar samma händelse. Klienten måste ladda om och kontrollera aktuell post innan ett manuellt återförsök. Ingen automatiskt omsänd mutation får användas.

Om själva commit-kvittensen försvinner skrivs `CommitUncertain` i stället för att påstå att ändringen misslyckats. Kontrollera aktuell post och eventuellt beständig `Committed`-händelse med samma operations-ID. Ett anslutningsfel under commit kan betyda både genomförd och återställd transaktion.

Identiska återleveranser dedupliceras genom `(EventId, PayloadHash)`, utan att skrivaren läser tabellen. Olika innehåll med samma händelse-ID blir separata, synliga poster; det får inte skrivas över eller döljas. Normal leverans återanvänder samma outboxhändelse och innehåll. En ej tillgänglig auditdatabas blockerar nya skyddade operationer. Outboxen är leveransstatus, inte den auktoritativa historiken.

## Filtrera på resurs och resursgrupp

Auditvyn har separata val för **Resurs** och **Resursgrupp**, samt **Inkludera undergrupper** (förvalt). De kan kombineras med datum, aktör och operation. Samtliga val kombineras med AND. En grupp utan undergrupper visar gruppens egna händelser och dess direkt tillhörande resurser. Med undergrupper inkluderas historiska efterkommande grupper och deras resurser. Exportera denna sida använder samma filter och sidposition som listan, högst 200 händelser.

Tjänsten fångar resursens ID/namn och gruppens hela kedja av ID/namn när händelsen skapas. Dessa metadata ingår i den signerade händelsen (format 3) innan den sparas i outboxen; lösenord och licensnycklar ingår aldrig. Lösenords-, licens-, LDAP-test-, behörighets- och ägarhändelser kopplas till sin resurs eller grupp. Flyttens avsikt registreras i ursprungsgruppen och dess genomförande i destinationsgruppen. Tidigare händelser ändras aldrig vid flytt, namnbyte eller borttagning. Misslyckade ändringar behåller avsiktens ursprungliga kontext.

Valbara objekt hämtas från verifierade, signerade historiska händelser via `GET /api/audit/targets`. Senast registrerat namn för samma ID används i vallistan; varje rad och export behåller sitt namn vid händelsens tidpunkt. Borttagna objekt förblir valbara. Auditläsarrollen krävs för både listor, filterval och export; filtret tilldelar ingen läs- eller ändringsrätt till själva valvet. En auditläsare kan därför filtrera historiken även utan resursbehörigheter.

API-parametrar: `resourceId`, `groupId` och `includeSubgroups=true|false` för både `GET /api/audit` och `GET /api/audit/export`. Gruppfiltret använder den signerade historiska gruppkedjan; ingen join mot dagens valvstruktur görs. Databasanropen är parameteriserade SELECT-anrop på auditläsarens anslutning. Audittransporten använder fortsatt enbart INSERT och audit körs fortsatt i samma tjänst med separat databas. Inga nya databaskolumner behövs. SQL-läsningen tolkar inte JSON och gör inget urval via osignerade sökprojektioner. Tjänsten verifierar kandidaterna innan filtrering, deduplicering och sidindelning; senaste namn och sortering avgörs av signerad tid och händelse-ID. En ogiltig representant kan därför inte tränga undan giltig historik. Korrupta eller för stora payloads visas som `Audit.InvalidPayload` med ogiltig signatur och utan opålitlig metadata. Sådana rader visas även vid filtrering, eftersom deras tillhörighet inte kan verifieras. Vallistan redovisar `invalidEventCount` och använder enbart giltiga händelser. Om vallistan misslyckas kan gränssnittet ändå hämta auditlistan.

Läsningen verifierar hela kandidatmaterialet för varje anrop. Den är avsiktligt begränsad: `AuditDatabase:MaximumReadEvents` är förvalt 100 000 (högst 1 000 000), `MaximumReadBytes` är förvalt 64 MiB räknat som UTF-16-payload och varje enskild payload får högst 262 144 tecken innan den markeras ogiltig. `CommandTimeoutSeconds` begränsar både databasarbete och verifieringsslingan. Överskridna totalgränser ger ett uttryckligt fel, aldrig ett tyst trunkerat resultat. Belastningsprova representativ historik; större installationer behöver en separat betrodd index-/arkivlösning innan dessa gränser kan hanteras effektivt.

Format 2 kan fortfarande verifieras byte för byte. Äldre händelser utan historisk kontext, och tjänsteövergripande händelser som identitetskontroller/export, visas vid ofiltrerad sökning men ingår inte i resurs-/gruppfilter. Ingen bakåtberäkning från aktuell struktur eller omsignering av äldre data sker.

## Signering och skyddets gränser

Händelsens hela serialiserade innehåll hashberäknas med SHA-256 och signeras med RSA-PSS/SHA-256 och ett separat auditcertifikat, minst RSA-3072. Privatnyckeln finns i Windows certifikatlager med åtkomst begränsad till tjänsteidentiteten. Gamla publika certifikat bevaras för verifiering efter rotation. Valvets krypteringscertifikat och auditcertifikatet ska vara olika.

Signering sker **när tjänsten skapar händelsen, innan den sparas i valvets outbox**. Leverans verifierar redan signerade, exakta payload-bytes, installations-ID och signatur. Transporten signerar aldrig om innehåll som har lästs från databasen. Redigerad outbox får därför inte omvandlas till betrodd audit. Även outboxens projektioner och leveransstatus omfattas av valvets externa integritetskontrollpunkt. Manipulation eller radering stoppar skyddade operationer innan signering, dekryptering eller leverans.

Auditvyn verifierar signaturen för varje returnerad händelse. En databasadministratör utan signeringsnyckeln kan inte ändra det signerade innehållet utan att verifieringen misslyckas. Sökindexens kolumner är endast projektioner; signerad händelse är underlaget för verifiering.

**INSERT-only innebär inte att historiken är omöjlig att radera.** DBA kan radera poster, återställa en äldre backup eller ändra rättigheter. Signering av individuella poster bevisar inte att inga poster saknas. En angripare med kontroll över applikationens tjänsteidentitet och signeringsnyckel kan skapa giltigt signerade falska händelser. Starkare bevis mot radering kräver separat skyddade backups eller externa/WORM-checkpoints. Detta ingår inte som garanti i denna arkitektur.

Ingen automatisk gallring utförs. Organisationen ska fastställa lagringstid, åtkomst, backup och eventuell personuppgiftshantering innan produktionsdrift. Valvras runtime-konton får inte ges DELETE för att utföra gallring; sådan hantering är en separat administrativ process.
