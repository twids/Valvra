# Databaser och Windows Auth

## Anslutningsmodell

Windows SSO identifierar webbanvändaren. Databasoperationer sker som **tjänstens Windows-identitet**, inte som webbanvändaren. Användarbehörigheter avgörs alltid i Valvra. Kerberos-delegering av användare till databasen behövs inte och konfigureras inte.

För MSSQL används `Integrated Security=true;Encrypt=true;TrustServerCertificate=false`. Kör IIS-poolen som en domänidentitet, helst gMSA. SQL Server måste känna igen kontot och databascertifikatet måste vara betrott och stämma med serverns DNS-namn. Anslut aldrig som SQL Server-administratör under normal drift.

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

## Auditdatabasens identiteter

Auditskrivaren använder tjänstens identitet och rollen `valvra_audit_writer`: endast INSERT på audittabellen. Auditläsaren använder `valvra_audit_reader`: endast SELECT. Roller och tabell skapas med skripten i `deploy/` eller installationsguidens schemainstallation.

Om båda kopplingarna använder samma Windows-identitet kan de inte ha motsatta rättigheter. För Windows-inloggning i båda kopplingarna konfigureras därför ett **separat Windows-läskonto**. Valvra använder en begränsad nätverksinloggning (`LOGON32_LOGON_NEW_CREDENTIALS`) endast runt auditläsning. Användarens Windows-identitet vidarebefordras inte. Läskontots domän, användarnamn och lösenord lagras i DPAPI-skyddad installationskonfiguration. Ett separat SELECT-only databaskonto är ett alternativ.

Installationsguiden kontrollerar INSERT/SELECT/UPDATE/DELETE/ALTER eller TRUNCATE-rättigheter. Dessa kontroller ersätter inte DBA:s granskning av bredare roller, schemaägarskap eller möjlighet att ändra behörigheter. Se [auditguiden](AUDIT.md) för hotmodell och återleverans.

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
