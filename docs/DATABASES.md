# Databases and Windows Auth

## Connection model

Windows SSO identifies the web user. Database operations run as **the service's Windows identity**, rather than the web user. User permissions are always determined in Valvra. Kerberos delegation of users to the database is neither required nor configured.

For MSSQL, use `Integrated Security=true;Encrypt=true;TrustServerCertificate=false`. Run the IIS pool as a domain identity, preferably dMSA or gMSA. SQL Server must recognize the account, and the database certificate must be trusted and match the server's DNS name. Never connect as a SQL Server administrator during normal operation.

dMSA was introduced in **Windows Server 2025** and requires the Windows/AD environment to be prepared for this account type and the IIS host to be authorized to use the account. Follow Microsoft's [dMSA instructions](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/delegated-managed-service-accounts/delegated-managed-service-accounts-overview) before installing Valvra. `Install-Iis.ps1` uses an empty password for managed service accounts whose names end in `$`; the script does not create dMSA in AD or configure its Windows policy. Verify IIS sign-in, SQL Integrated Security and the HTTP SPN with the AD/DBA administrator. Operation with an actual dMSA account has not yet been verified in the project's test environment.

PostgreSQL can use password authentication or SSPI/GSSAPI with the service's Windows identity. For the latter, the DBA must configure PostgreSQL's `pg_hba.conf`, Kerberos/SSPI and account mapping; specify the appropriate PostgreSQL role during setup. TLS requires `SSL Mode=VerifyFull`. Merely choosing Windows in the web interface is insufficient if the database server has not been prepared for SSPI/GSSAPI.

## Vault database runtime permissions

The runtime identity needs SELECT, INSERT, UPDATE and DELETE on Valvra's tables, plus SELECT on the migration history. It must not own the database, create/alter schemas or be an administrator. Create the schema using the migration account first. SQL Server example, after creating the login and database user:

```sql
USE [Valvra];
CREATE ROLE valvra_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO valvra_runtime;
-- The dbo schema must contain only Valvra's tables in this dedicated database.
ALTER ROLE valvra_runtime ADD MEMBER [EXAMPLE\svcValvra$];
```

For PostgreSQL, after running migrations and creating the `valvra_runtime` role:

```sql
GRANT CONNECT ON DATABASE valvra TO valvra_runtime;
GRANT USAGE ON SCHEMA public TO valvra_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO valvra_runtime;
-- No identity sequences are needed: records use application-generated UUIDs.
```

## Audit database service account

The default installation uses **one service account** for both inserts and reads, with `valvra_audit_runtime`: only SELECT and INSERT on the audit table. Roles and the table are created using the scripts in `deploy/` or the installation wizard's schema installation. As DBA, create the SQL Server login once and a user in each database. Example after running the audit script:

```sql
-- Run as DBA, not as Valvra's service account.
CREATE LOGIN [EXAMPLE\svcValvra$] FROM WINDOWS;
USE [Valvra];
CREATE USER [EXAMPLE\svcValvra$] FOR LOGIN [EXAMPLE\svcValvra$];
ALTER ROLE valvra_runtime ADD MEMBER [EXAMPLE\svcValvra$];
USE [ValvraAudit];
CREATE USER [EXAMPLE\svcValvra$] FOR LOGIN [EXAMPLE\svcValvra$];
ALTER ROLE valvra_audit_runtime ADD MEMBER [EXAMPLE\svcValvra$];
```

Create `valvra_runtime` as described above before assigning the role. Do not create logins/users again if they already exist. Choose Windows / Integrated Security for the vault and audit databases in setup; leave **Use a separate audit reader** unchecked. Valvra then stores no additional Windows credentials. The same login has different permissions in the two databases. Do not grant the account database/schema/table ownership, administrative roles, permission administration or broader privileges through AD groups/procedures. The MSSQL script denies UPDATE/DELETE/ALTER/TAKE OWNERSHIP on the audit table and ALTER/TAKE OWNERSHIP on its schema; do not grant specific column-level UPDATE permissions. CONTROL must not be granted. DENY CONTROL is not used because it would also block the required SELECT/INSERT permissions.

Separate identities remain an optional alternative: `valvra_audit_writer` for INSERT and `valvra_audit_reader` for SELECT. Enable a separate audit reader in setup. A separate Windows reader account uses a restricted network logon (`LOGON32_LOGON_NEW_CREDENTIALS`) around audit reads, and its credentials are stored in DPAPI-protected configuration. This is unnecessary for the default installation with one dMSA/gMSA account. The user's Windows identity is not forwarded. A separate SELECT-only database account is also possible.

The installation wizard requires INSERT on the writer connection and SELECT on the reader connection; both may have both permissions. It rejects UPDATE/DELETE/ALTER or TRUNCATE, including column-level UPDATE, and certain schema/ownership/administration permissions. Unknown results are rejected. These checks do not replace the DBA's review of broader roles, schema ownership or the ability to alter permissions. PostgreSQL roles lack SQL Server's DENY: broader permissions must not be inherited, and the runtime account must not be able to assume administrative roles. See [the audit guide](AUDIT.md) for the threat model and redelivery.

## Migrations from source

Run as the installation identity. The connection string is read from the process environment; do not put passwords in command-line arguments or store them in the repository:

```powershell
dotnet tool restore
$env:VALVRA_DB_PROVIDER = 'SqlServer' # or PostgreSql
$env:VALVRA_MIGRATION_CONNECTION = 'Server=db.example.se;Database=Valvra;Integrated Security=true;Encrypt=true;TrustServerCertificate=false'
dotnet ef database update --project src/Valvra.Migrations.SqlServer --startup-project src/Valvra.Web
Remove-Item Env:VALVRA_MIGRATION_CONNECTION
```

Choose `src/Valvra.Migrations.PostgreSql` for PostgreSQL. Back up the database before upgrades. Runtime startup never runs migrations automatically.
