# Operations, backup and recovery

## What must be backed up

1. The vault database, including encrypted data keys and the audit outbox.
2. The audit database and its backup history under separate access control.
3. All encryption certificate private keys in password-protected PFX backups.
4. The audit certificate's private key and older public certificates for signature verification.
5. The integrity certificate's private key and older public certificates, plus all of `App_Data`, including `installation.bin` and `Integrity/checkpoint.bin`.
6. A Windows system/machine backup that preserves the DPAPI machine keys, and documented service identities/permissions in the organization's protected documentation.

PFX backups and their passwords must be stored separately from database backups. **Stored passwords cannot be recovered without the encryption key.** Both `installation.bin` and the checkpoint are protected by machine-bound Windows DPAPI. A database, PFX and copied `App_Data` alone are insufficient for recovery on a new Windows installation.

Stop the IIS application pool and any operator commands when backing up the vault database and checkpoint. Take a coordinated copy of these before restarting the service. A database backup from a different generation than the checkpoint is deliberately rejected. Back up the audit database separately, and preserve any existing later audit history when restoring the vault. Document which copies belong together and protect backup access separately from database runtime accounts.

## Restoring the same protected installation

Stop the service. Restore a verified, coordinated pair of vault database and checkpoint to the Windows installation that can decrypt the DPAPI files. Restore the required certificates to LocalMachine/My and restrict private key access to the service identity. Preserve the installation ID and previous key identifiers. Start the service and verify permissions, old and new versions, and audit using synthetic records.

Restoring to another machine requires verified restoration of the Windows machine's DPAPI context, for example through the organization's machine backup. **Portable checkpoint export/import to a new Windows installation is not implemented.** Setup cannot approve an existing database without a valid checkpoint. Do not create a new empty checkpoint over restored data or delete the checkpoint file to bypass an integrity error.

Rehearse this on another server before the first production deployment. A backup that has not been restored is not verified.

## Interrupted integrity update

The checkpoint can contain an approved state and a prepared next state. If the database exactly matches the prepared state, the service completes the checkpoint automatically. If it matches the older state, access is blocked; the service does not guess whether this was an interruption or a rollback attack.

After separately checking the interruption, database and audit, an AD access administrator with the application pool stopped and appropriate certificate/database access may run:

```powershell
C:\inetpub\Valvra\Valvra.Web.exe --recover-integrity --discard-uncommitted
```

The command removes a prepared checkpoint only if the database's exact hash still matches the signed previous checkpoint. The operator must have a permanent, person-bound Access Administrator role in that verified vault snapshot; AD group membership or legacy directory administrator flags do not grant this role. The account must still be enabled in AD. It does not approve arbitrary data or recreate a missing checkpoint. Operator recovery is audited. For other discrepancies, investigate the cause and use a verified coordinated backup.

## Changing the installation

### Update to 1.2.2

Use `Valvra-1.2.2-win-x64.zip` and follow the complete [in-place upgrade checklist](#in-place-upgrade-checklist) below. Version 1.2.2 adds advanced database connection options to setup and requires **no database schema changes or data conversion** from 1.0.x, 1.1.0, 1.2.0 or 1.2.1. See [the dependency baseline](DEPENDENCIES.md) for reviewed versions and the .NET 10 compatibility exception. Version 1.2.0 introduced diagnostic file logging and setup troubleshooting. Logging is enabled by default under `App_Data/Logs`; ensure the application pool identity has write permission, keep logs outside the web root, and review size and retention settings below. After upgrading, also verify an import with synthetic data in a group with the intended inherited access; see [the import guide](IMPORT.md).

### Update from 1.0.0 to 1.0.1

Use `Valvra-1.0.1-win-x64.zip` and follow the same checklist below. This patch also requires **no database schema changes or data conversion** from 1.0.0.

### In-place upgrade checklist

This procedure updates an existing Windows/IIS installation on the **same Windows installation**, using the same databases and service identity. It applies to the version paths listed above. For other versions, check their release notes for schema changes and rollback compatibility before proceeding. Moving to another server is a recovery operation; see [restoring the same protected installation](#restoring-the-same-protected-installation).

**Do not run `Install-Iis.ps1`, rerun setup, or use `--initialize-setup` for a normal upgrade.** Do not delete or replace `App_Data`, generate new certificates, or create a new vault.

#### 1. Prepare and verify the package

1. Schedule a maintenance window and ask users to finish their work. Record the currently installed version and the target version.
2. Record the IIS website's physical path, application pool name, service identity, HTTPS binding and Windows Authentication settings, including Extended Protection. The examples below use `C:\inetpub\Valvra` and application pool `Valvra`; substitute your actual values.
3. Download the target release ZIP and its `SHA256SUMS.txt`. In PowerShell, calculate the ZIP's hash, for example:

   ```powershell
   Get-FileHash -LiteralPath 'C:\Temp\Valvra-1.2.2-win-x64.zip' -Algorithm SHA256
   ```

   Compare the complete hash with the entry for that ZIP in `SHA256SUMS.txt`. Stop if they differ.
4. Extract the package to a separate staging directory outside the website. Identify the application directory containing `Valvra.Web.exe`, its DLLs, `web.config` and `wwwroot`. Do not extract the ZIP directly over the running installation.

#### 2. Stop writes and take coordinated backups

1. Stop any Valvra operator commands and the website's application pool. In **Windows PowerShell as administrator**:

   ```powershell
   Import-Module WebAdministration
   Stop-WebAppPool -Name 'Valvra'
   Get-WebAppPoolState -Name 'Valvra'
   ```

   Confirm the pool is stopped and its worker process has exited before copying files or backing up. Keep the pool stopped through step 3.
2. Follow [what must be backed up](#what-must-be-backed-up): take a coordinated backup of the vault database and all of `App_Data`, and back up the audit database separately. Include certificate backups and the Windows machine/DPAPI recovery material described there. Label the matching vault database and checkpoint backups together.
3. Save a complete copy of the current website files **outside the website directory**, with their NTFS permissions, for application rollback. Include local configuration such as `appsettings.json`, any `appsettings.Production.json`, and `web.config`. Protect this copy as installation data; it does not replace the database, certificate or machine backups.

#### 3. Replace application files and retain installation settings

1. In the existing website directory, replace the application binaries and static assets with those from the staged application directory. Copy the full set of packaged application files and the contents of `wwwroot`, rather than only `Valvra.Web.exe`. Keep the existing directory and its NTFS permissions.
2. **Exclude the existing `App_Data` directory and all its contents from replacement or deletion.** This preserves `installation.bin`, `Integrity/checkpoint.bin`, the installation ID and protected settings. Keep the existing certificates in `LocalMachine/My` and their private-key permissions.
3. Treat configuration files separately: compare the package's `appsettings*.json` and `web.config` with the saved installed copies before replacing them. Retain local values, including `AllowedHosts`, and any IIS customizations; merge new release defaults where needed. Use the target release's application hosting configuration while retaining the site's authentication and protection settings. Do not blindly overwrite local configuration with package defaults.
4. Keep the same application pool, service identity, HTTPS binding, Windows Authentication and Extended Protection settings. Check that the service identity can still read the application files and modify `App_Data`, and retain the IIS rule that blocks HTTP access to `App_Data`.
5. For the version paths listed above, **do not run database migrations or schema installation**. Runtime startup never applies migrations automatically. Future releases requiring migrations must provide their own instructions and use a separate migration account; see [databases](DATABASES.md#migrations-from-source).

#### 4. Restart and verify before reopening access

Start the existing application pool:

```powershell
Start-WebAppPool -Name 'Valvra'
Get-WebAppPoolState -Name 'Valvra'
```

From a domain-joined client, verify the following with authorized test accounts and synthetic records:

- Windows SSO works, and IIS still has the required Extended Protection setting. If the setup wizard appears unexpectedly, stop the pool and investigate the installation files instead of completing setup.
- `App_Data` files cannot be downloaded over HTTP, including when signed in.
- Settings access and resource permissions work as expected, including denial of secret access to an account without read permission.
- Existing passwords and licenses, including previous versions, can be read by an authorized account. Creating and updating a synthetic record succeeds.
- Signed audit records can be verified, and `/api/health` succeeds for an access administrator without a growing audit outbox. HTTP 200 alone is insufficient.
- Any release-specific checks pass, including the synthetic import check for 1.1.0 described above.

Check IIS/application errors and record the installed package version and verification results. Reopen access only after these checks pass. Keep the previous application files and coordinated backups according to your recovery policy.

#### 5. Roll back application files if verification fails

For the version paths listed above, no schema or data conversion is involved:

1. Stop the application pool and operator commands again.
2. Replace the new application binaries and static assets with the saved previous application's files. Remove files introduced by the failed release from the application file set, using the staged package to identify them. Restore the previous application configuration and retain the existing NTFS permissions.
3. **Keep the current `App_Data`, vault database, audit database, certificates and installation identity.** Do not restore an older checkpoint or database as part of an application-only rollback; verification may already have created new records and audit events.
4. Restart the same pool and repeat the sign-in, permissions, existing-version and audit checks above.

If the failure concerns data integrity or the previous application cannot use the current data, stop and investigate. A data restore is a separate [coordinated recovery procedure](#restoring-the-same-protected-installation), preserving later audit history; it must not mix database and checkpoint generations.

### Reconfigure protected installation settings

On the server, as an authorized operator:

```powershell
C:\inetpub\Valvra\Valvra.Web.exe --initialize-setup
```

Open `/setup`, enter the new one-time code and fill in the settings. The wizard does not return previous passwords or private configuration to the browser. Restart the application pool after saving. Reconfiguration replaces the saved configuration; therefore, retain existing settings and previous key identifiers in protected operator documentation.

## Certificate rotation

Keep older encryption certificates until all data keys have been rewrapped and the backup recovery period has elapsed. Older public audit certificates are needed as long as older audit records must be verified. Older integrity certificates are needed for the current and backed-up checkpoint. TLS certificate rotation is separate from these three certificates.

Do not change the active encryption certificate without also retaining old thumbprints in `KeyProtection:AllowedThumbprints`. Older audit certificates are listed in `AuditDatabase:VerificationCertificateThumbprints`. These lists must be retained during reconfiguration. Key administration must be audited.

For integrity, list previous thumbprints in `Integrity:VerificationCertificateThumbprints`. Retain the same installation ID. Reconfiguration verifies the existing database's checkpoint; it does not reinitialize the vault. The next protected write signs the next generation with the active integrity certificate.

After configuring new and old certificates and verifying vault recovery, stop the website's application pool during a maintenance window and run `Valvra.Web.exe --rewrap-keys` from the publish directory. The operator must be an enabled AD person account with the vault's Access Administrator role and have access to the certificates' private keys and the databases. Directory group membership alone does not authorize this command. The command rewraps data keys and audits each record; it does not need to decrypt password values. If interrupted, it can be rerun: records already using the active key are skipped. Still retain previous key backups for older database backups. Then start the application pool and verify new and old secret versions.

## Outages and monitoring

### Diagnostic file logs

Valvra uses Serilog to write newline-delimited JSON diagnostics. By default files are
stored in `App_Data/Logs/valvra-YYYYMMDD.jsonl` beside the executable. If
`VALVRA_CONFIG_DIR` is set, relative log paths are resolved under that directory.
Size rolls add a sequence suffix. Logs are outside `wwwroot`; the IIS installer
already grants the service identity Modify access to `App_Data` and restricts access
to the service identity, SYSTEM and administrators.

Configure `FileLogging` in the deployed `appsettings.json`, or use environment
variables such as `FileLogging__Path`. Restart the application pool after changes.
These settings are read before the encrypted installation configuration so startup
failures can be recorded. They are operator settings, not settings in the web UI.

```json
"FileLogging": {
  "Enabled": true,
  "Path": "Logs/valvra-.jsonl",
  "MinimumLevel": "Information",
  "FileSizeLimitBytes": 10485760,
  "RetainedFileCountLimit": 31
}
```

`Path` can be absolute, for example `D:/ValvraLogs/valvra-.jsonl`. Create a private
directory and grant the IIS service identity Modify access when using a custom
location. Do not place logs under a public directory, network share exposed to
users, or a link into `wwwroot`. The application rejects paths lexically inside
its web root. Preserve custom logging settings during upgrades.

Files roll daily and at the configured size (10 MiB by default). Retention is a
**file count**, including size rolls, not a number of days. The default retains 31
files, roughly 310 MiB plus a possible final event. Cleanup happens when rolling;
stopped applications do not run cleanup. A single event can exceed the size limit.
Use `Warning` or `Error` to reduce volume; `Information` includes every request.
`Enabled: false` disables the diagnostic file sink. Settings are validated at
startup, including a write probe. An invalid or unwritable destination prevents
startup and emits a generic console error. Later sink failures (for example a full
disk) emit a generic console error without sensitive Serilog SelfLog details;
they do not block vault operations. Monitor both disk capacity and log freshness.
IIS stdout capture can be enabled temporarily to collect these console errors.

Events include UTC-offset timestamps, severity, process ID, startup/shutdown,
setup check outcomes, request method, route template, response status, duration
and correlation ID. Responses include `X-Correlation-ID`; error responses also
include `correlationId`. Use that ID to find related request and failure records.
Canceled requests are recorded as diagnostic status 499, without changing the
HTTP response. Static files and unknown routes are recorded as `unmatched`.

Failure diagnostics include bounded inner-exception types, HRESULTs, stack method
locations, and database/LDAP error codes where available. Exception messages,
exception Data, SQL, connection strings, raw URLs, query strings, request/response
bodies, headers, cookies and user identities are excluded. The file sink receives
only controlled diagnostic events, not arbitrary framework `ILogger` messages or
ambient logging scopes. Existing framework logging remains separate. Changing the
minimum level does not enable sensitive dumps. Diagnostics do not replace the
signed audit database or prove that an operation did not commit.

On the server, inspect the latest file with PowerShell:

```powershell
$latest = Get-ChildItem 'C:\inetpub\Valvra\App_Data\Logs\*.jsonl' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content -LiteralPath $latest.FullName -Tail 50 -Wait
```

To find a correlation ID across retained files:

```powershell
Get-ChildItem 'C:\inetpub\Valvra\App_Data\Logs\*.jsonl' |
    Select-String -SimpleMatch 'CORRELATION-ID-FROM-RESPONSE'
```

An audit database outage blocks secret release and new protected changes. An error after a local commit can mean that the change has already been applied. Check the current record before another manual attempt. The outbox is redelivered before the next protected operation.

`/api/health` requires Windows SSO and access administrator permission. The endpoint checks audit using an installation-like event and shows database connectivity and the number of remaining outbox records. Alert on HTTP errors, a growing outbox, certificate problems and failed backups. HTTP 200 alone does not prove full recovery.

Run one IIS worker process for the application pool. The LDAP test rate limit is local to that process and resets on restart. Do not run automatic LDAP tests against production accounts.
