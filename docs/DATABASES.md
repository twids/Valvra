# Databaser och Windows Auth

## Anslutningsmodell

Windows SSO identifierar webbanvändaren. Databasoperationer sker som **tjänstens Windows-identitet**, inte som webbanvändaren. Användarbehörigheter avgörs alltid i Valvra. Kerberos-delegering av användare till databasen behövs inte och konfigureras inte.

För MSSQL används `Integrated Security=true;Encrypt=true;TrustServerCertificate=false`. Kör IIS-poolen som en domänidentitet, gärna dMSA eller gMSA. SQL Server måste känna igen kontot och databascertifikatet måste vara betrott och stämma med serverns DNS-namn. Anslut aldrig som SQL Server-administratör under normal drift.

dMSA infördes i **Windows Server 2025** och kräver att Windows/AD-miljön förbereds för kontotypen och att IIS-värden tillåts använda kontot. Följ Microsofts [dMSA-anvisningar](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/delegated-managed-service-accounts/delegated-managed-service-accounts-overview) innan Valvra installeras. `Install-Iis.ps1` använder tomt lösenord för hanterade tjänstekonton med namn som slutar på `$`; skriptet skapar inte dMSA i AD eller konfigurerar dess Windows-policy. Verifiera IIS-inloggning, SQL Integrated Security och HTTP-SPN med AD/DBA-administratören. Körning mot ett faktiskt dMSA-konto är ännu inte verifierad i projektets testmiljö.

PostgreSQL kan använda lösenordsinloggning eller SSPI/GSSAPI med tjänstens Windows-identitet. För det senare måste DBA konfigurera PostgreSQLs `pg_hba.conf`, Kerberos/SSPI och kontomappning; rätt PostgreSQL-roll anges i installationen. TLS kräver `SSL Mode=VerifyFull`. Det är inte tillräckligt att bara välja Windows i webbgränssnittet om databasservern inte är förberedd för SSPI/GSSAPI.

## Valvdatabasens runtime-rättigheter

Runtime-identiteten behöver SELECT, INSERT, UPDATE och DELETE på Valvras tabeller samt SELECT på migrationshistoriken. Den får inte äga databasen, skapa/ändra scheman eller vara administratör. Skapa schemastrukturen genom migrationskontot först. Exempel för SQL Server, efter att login och databasuser skapats:

```sql
USE [Valvra];
CREATE ROLE valvra_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO valvra_runtime;
-- Schema dbo ska endast innehålla Valvras tabeller i denna dedikerade databas.
ALTER ROLE valvra_runtime ADD MEMBER [EXAMPLE\svcValvra$];
```

För PostgreSQL, efter migrationskörningen och efter att `valvra_runtime`-rollen skapats:

```sql
GRANT CONNECT ON DATABASE valvra TO valvra_runtime;
GRANT USAGE ON SCHEMA public TO valvra_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO valvra_runtime;
-- Inga identitetssekvenser behövs: poster använder applikationsgenererade UUID:n.
```

## Auditdatabasens tjänstekonto

Standardinstallationen använder **ett tjänstekonto** för både tillägg och läsning, med `valvra_audit_runtime`: endast SELECT och INSERT på audittabellen. Roller och tabell skapas med skripten i `deploy/` eller installationsguidens schemainstallation. Som DBA, skapa SQL Server-login en gång och en user i respektive databas. Efter att auditskriptet körts, exempel:

```sql
-- Kör som DBA, inte som Valvras tjänstekonto.
CREATE LOGIN [EXAMPLE\svcValvra$] FROM WINDOWS;
USE [Valvra];
CREATE USER [EXAMPLE\svcValvra$] FOR LOGIN [EXAMPLE\svcValvra$];
ALTER ROLE valvra_runtime ADD MEMBER [EXAMPLE\svcValvra$];
USE [ValvraAudit];
CREATE USER [EXAMPLE\svcValvra$] FOR LOGIN [EXAMPLE\svcValvra$];
ALTER ROLE valvra_audit_runtime ADD MEMBER [EXAMPLE\svcValvra$];
```

Skapa `valvra_runtime` enligt avsnittet ovan före rolltilldelningen. Kör inte login-/user-skapande igen om de redan finns. Välj Windows / Integrated Security för valv- och auditdatabasen i setup; lämna **Använd separat auditläsare** avmarkerat. Valvra sparar då ingen extra Windows-inloggning. Samma login har olika rättigheter i de två databaserna. Ge inte kontot databas-/schema-/tabellägarskap, administrativa roller, rättighetsadministration eller bredare behörigheter via AD-grupper/procedurer. MSSQL-skriptet nekar UPDATE/DELETE/ALTER/TAKE OWNERSHIP på audittabellen och ALTER/TAKE OWNERSHIP på dess schema; ge inga särskilda UPDATE-rättigheter på kolumner. CONTROL ska inte tilldelas. Ett DENY CONTROL används inte eftersom det även skulle blockera nödvändig SELECT/INSERT.

Separata identiteter är ett valfritt alternativ: `valvra_audit_writer` för INSERT och `valvra_audit_reader` för SELECT. Aktivera separat auditläsare i setup. Vid separat Windows-läskonto används en begränsad nätverksinloggning (`LOGON32_LOGON_NEW_CREDENTIALS`) runt auditläsning och läskontots uppgifter lagras i DPAPI-skyddad konfiguration. Detta behövs inte för standardinstallationen med ett dMSA/gMSA-konto. Användarens Windows-identitet vidarebefordras inte. Ett separat SELECT-only databaskonto är också möjligt.

Installationsguiden kräver INSERT på skrivkopplingen och SELECT på läskopplingen; båda får ha båda rättigheterna. Den nekar UPDATE/DELETE/ALTER eller TRUNCATE, även kolumn-UPDATE, samt vissa schema-/ägar-/administrationsrättigheter. Okända resultat nekas. Dessa kontroller ersätter inte DBA:s granskning av bredare roller, schemaägarskap eller möjlighet att ändra behörigheter. PostgreSQL-roller saknar SQL Servers DENY: inga bredare rättigheter får ärvas och runtime-kontot får inte kunna ta över administrativa roller. Se [auditguiden](AUDIT.md) för hotmodell och återleverans.

## Migrationer från källkod

Kör som installationsidentitet. Anslutningssträngen läses från processmiljön och ska inte skrivas med lösenord i kommandoradens argument eller sparas i repot:

```powershell
dotnet tool restore
$env:VALVRA_DB_PROVIDER = 'SqlServer' # eller PostgreSql
$env:VALVRA_MIGRATION_CONNECTION = 'Server=db.example.se;Database=Valvra;Integrated Security=true;Encrypt=true;TrustServerCertificate=false'
dotnet ef database update --project src/Valvra.Migrations.SqlServer --startup-project src/Valvra.Web
Remove-Item Env:VALVRA_MIGRATION_CONNECTION
```

Välj `src/Valvra.Migrations.PostgreSql` vid PostgreSQL. Ta databasbackup före uppgraderingar. Runtime-start kör aldrig migrationer automatiskt.
