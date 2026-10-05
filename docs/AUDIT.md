# Audit: samma tjänst, separat databas

Valvra skriver audit direkt från webbapplikationens .NET-process till en **separat databas**. Ingen separat auditserver, HTTP-mottagare eller bakgrundstjänst behöver installeras. Valvras ordinarie databas och auditdatabasen får ligga på samma databasserver, men ska ha separata databaser och behörighetsidentiteter.

## Tre olika databasanslutningar

| Anslutning | Användning | Runtime-behörighet |
| --- | --- | --- |
| Valvra | Valv, resurser, tilldelningar och transaktionell audit-outbox | Läs/skriv i Valvras tabeller; ingen schemaadministration |
| Audit writer | Lägga till signerade audithändelser | Endast `INSERT` på audittabellen |
| Audit reader | Auditvyn, filtrering och export | Endast `SELECT` på audittabellen |

Skrivaren behöver **inte** SELECT, UPDATE, DELETE, sekvensåtkomst, OUTPUT, RETURNING eller schemaändringsrättigheter. Läsaren är en separat identitet och anslutningssträng, även om båda används av samma .NET-tjänst. Migrations-/installationsidentiteten får aldrig användas som runtime-identitet. Använd inte `db_owner`, `sysadmin`, tabellägare eller PostgreSQL-superuser.

En Windows-tjänsteidentitet som autentiserar med samma integrerade SQL-identitet för både läsning och skrivning ger inte denna uppdelning. Använd separata databasidentiteter för auditanslutningarna, med deras autentiseringsmaterial i installationsmiljöns skyddade konfiguration. Lägg inte anslutningssträngar med lösenord i Git.

Installationsskript: [MSSQL](../deploy/audit-sqlserver.sql) och [PostgreSQL](../deploy/audit-postgresql.sql). Kör dem som DBA i auditdatabasen. Bevara den definierade primärnyckeln och lägg inte till andra unika index utan att ändra återleveranshanteringen.

## Leverans och avbrott

Varje händelse får ett UUID, UTC-tid, aktörens stabila provider/ID, mål-ID, operation, resultat och korrelations-ID. Lösenord, licensnycklar, kontolösenord, hemliga anteckningar och kryptografiska nycklar får aldrig förekomma i audit.

För utlämning av en hemlighet måste auditdatabasen ha kvitterat den beständiga INSERT-operationen innan dekryptering och svar. `ReleaseAuthorized` betyder serverns godkännande av utlämning, inte bevis på att klienten faktiskt mottagit eller läst hemligheten.

För ändringar skrivs först en avsiktshändelse. Därefter genomförs förändringen och resultathändelsen i en lokal transaktion i Valvras databas. Resultatet levereras till auditdatabasen innan framgång rapporteras till klienten. Databaserna använder inte en distribuerad transaktion.

Om ändringen har committats och resultataudit sedan misslyckas kan ändringen redan vara genomförd trots felmeddelandet. Den beständiga outboxen återlevererar samma händelse. Klienten måste ladda om och kontrollera aktuell post innan ett manuellt återförsök. Ingen automatiskt omsänd mutation får användas.

Om själva commit-kvittensen försvinner skrivs `CommitUncertain` i stället för att påstå att ändringen misslyckats. Kontrollera aktuell post och eventuellt beständig `Committed`-händelse med samma operations-ID. Ett anslutningsfel under commit kan betyda både genomförd och återställd transaktion.

Identiska återleveranser dedupliceras genom `(EventId, PayloadHash)`, utan att skrivaren läser tabellen. Olika innehåll med samma händelse-ID blir separata, synliga poster; det får inte skrivas över eller döljas. Normal leverans återanvänder samma outboxhändelse och innehåll. En ej tillgänglig auditdatabas blockerar nya skyddade operationer. Outboxen är leveransstatus, inte den auktoritativa historiken.

## Signering och skyddets gränser

Händelsens hela serialiserade innehåll hashberäknas med SHA-256 och signeras med RSA-PSS/SHA-256 och ett separat auditcertifikat, minst RSA-3072. Privatnyckeln finns i Windows certifikatlager med åtkomst begränsad till tjänsteidentiteten. Gamla publika certifikat bevaras för verifiering efter rotation. Valvets krypteringscertifikat och auditcertifikatet ska vara olika.

Auditvyn verifierar signaturen för varje returnerad händelse. En databasadministratör utan signeringsnyckeln kan inte ändra det signerade innehållet utan att verifieringen misslyckas. Sökindexens kolumner är endast projektioner; signerad händelse är underlaget för verifiering.

**INSERT-only innebär inte att historiken är omöjlig att radera.** DBA kan radera poster, återställa en äldre backup eller ändra rättigheter. Signering av individuella poster bevisar inte att inga poster saknas. En angripare med kontroll över applikationens tjänsteidentitet och signeringsnyckel kan skapa giltigt signerade falska händelser. Starkare bevis mot radering kräver separat skyddade backups eller externa/WORM-checkpoints. Detta ingår inte som garanti i denna arkitektur.

Ingen automatisk gallring utförs. Organisationen ska fastställa lagringstid, åtkomst, backup och eventuell personuppgiftshantering innan produktionsdrift. Valvras runtime-konton får inte ges DELETE för att utföra gallring; sådan hantering är en separat administrativ process.
