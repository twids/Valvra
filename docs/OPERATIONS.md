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

### Update from 1.0.x to 1.1.0

This release adds password import without database schema changes or data conversion. Follow the same coordinated backup and file replacement procedure below using the verified 1.1.0 package. Preserve `App_Data`, the installation ID, certificates, key permissions, service identity and IIS authentication configuration. Do not rerun the installation script or setup. Verify an import with synthetic data in a group with the intended inherited access; see [the import guide](IMPORT.md).

### Update from 1.0.0 to 1.0.1

This patch requires no database schema change or data conversion. Take a coordinated backup as described above, including the machine-bound protected files and certificate keys. Stop the IIS application pool and operator commands. Replace the application binaries and static files with the verified 1.0.1 package; **preserve the existing `App_Data` directory, installation ID, certificates, private-key permissions, service identity and IIS authentication configuration**. Do not run `Install-Iis.ps1` over an existing website or rerun setup to create a new vault.

Restart the same application pool. Verify sign-in, settings access, existing secret/license versions and signed audit using synthetic records. Keep the prior application files for rollback; roll back application binaries without replacing vault data or checkpoints with a different generation. Domain-specific IIS/AD verification remains required.

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

An audit database outage blocks secret release and new protected changes. An error after a local commit can mean that the change has already been applied. Check the current record before another manual attempt. The outbox is redelivered before the next protected operation.

`/api/health` requires Windows SSO and access administrator permission. The endpoint checks audit using an installation-like event and shows database connectivity and the number of remaining outbox records. Alert on HTTP errors, a growing outbox, certificate problems and failed backups. HTTP 200 alone does not prove full recovery.

Run one IIS worker process for the application pool. The LDAP test rate limit is local to that process and resets on restart. Do not run automatic LDAP tests against production accounts.
