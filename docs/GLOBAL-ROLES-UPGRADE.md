# Global content roles and explicit exclusions

This change follows v1.3.0 and requires a vault database migration. It does not change the audit database, encrypted envelopes, existing row serialization or the integrity hash format. Existing accounts are not promoted automatically. The installer retains its previous Access Administrator and System Administrator roles.

## Upgrade an existing IIS installation

1. Back up the vault database, audit database, deployed configuration, complete `App_Data`, certificates and recovery material. Verify the backups can be restored. Keep the old application package with the corresponding backups.
2. Stage and verify the new application package. Stop the IIS application pool and verify its worker process has exited. Keep the application stopped throughout migration and deployment.
3. From the source revision matching the new package, run `dotnet tool restore`. Use a separate DBA/migration identity with schema permissions. Never give the IIS runtime identity schema administration rights.
4. Set `VALVRA_DB_PROVIDER` to `SqlServer` or `PostgreSql`, and set `VALVRA_MIGRATION_CONNECTION` securely in that process environment. See [database connection and migration instructions](DATABASES.md#migrations-from-source). Do not place a password in shell history or command arguments.
5. Review the upgrade SQL before applying it:

   ```powershell
   # SQL Server: v1.3.0 schema to this change
   dotnet ef migrations script 20261005175119_IntegrityAndLicenseHistory 20261014155449_AccessExclusions --idempotent --project src/Valvra.Migrations.SqlServer --startup-project src/Valvra.Web --output upgrade-reviewed.sql
   dotnet ef database update 20261014155449_AccessExclusions --project src/Valvra.Migrations.SqlServer --startup-project src/Valvra.Web
   ```

   For PostgreSQL, use project `src/Valvra.Migrations.PostgreSql`, baseline `20261005175321_IntegrityAndLicenseHistory` and destination `20261014155601_AccessExclusions`. The migration adds the empty `Exclusions` table and a unique person/target index. It must not rewrite existing vault rows or integrity checkpoints. Remove `VALVRA_MIGRATION_CONNECTION` from the process environment afterwards.
6. Verify the runtime identity has SELECT, INSERT, UPDATE and DELETE on the new table, without ALTER or ownership. Existing schema-level grants may already cover it; installations using individual table grants require an explicit DBA grant. Preserve the audit database's append-only restrictions.
7. Deploy the staged application while preserving the installation's complete `App_Data` and protected configuration. Do not rerun setup, initialize a new vault or replace the integrity checkpoint. Start the application pool and verify sign-in, older secret/license versions, audit, integrity and content access.
8. In **Administration → Global roles**, explicitly assign **Global administrator** or **License administrator** to the intended individual accounts. Verify ordinary users retain their previous rights. Test import to an allowed destination, a denied destination, license administration without key reads, and explicit restoration of an exclusion.
9. If rollback is necessary, stop the application first and restore the old package together with the matching vault database and complete protected installation state. Do not downgrade only the schema or delete exclusion rows manually: exclusions are part of the integrity-protected snapshot. Audit history must remain preserved and append-only; coordinate database restoration and recovery with the documented audit/integrity procedure.

## Authorization contract

- Global administrator supplies all content permissions, access administration, system administration and audit reading, except for personal exclusions. Existing Access/System Administrator roles retain their previous meaning.
- License administrator supplies license metadata and modification/assignment rights only. The dedicated license destination endpoint exposes destination names/paths intentionally, without password titles, counts, owners or grants. Current and historical key reads, and secret-version restoration, still require actual resource Read permission. Metadata edits preserve the encrypted payload by default; replacing/clearing a key is explicit.
- Exclusions match the exact provider and individual account identifier. A group exclusion covers all descendant groups/resources. Exclusions precede ownership, group/direct grants and both new global roles; changing roles or adding grants does not remove them.
- Access administrators create exclusions only on targets they can manage. Restoration is separately authorized, revision checked and audited. An enabled Global Administrator can explicitly restore their own exclusions from Administration; opening the page does not restore anything. Other Access Administrators must retain Manage Access on the excluded target to restore an exclusion.
- Moves that would remove any inherited exclusion are rejected until that exclusion is explicitly restored. This prevents moving content out of a denied subtree to silently regain access.
- Deleting a group or resource with direct exclusions is rejected until each exclusion is explicitly restored through the audited restoration operation. Deletion cannot silently reopen access to historical audit records.
- License assignments into an excluded destination show only a redacted destination and seat usage; they cannot be reassigned/reclaimed through that excluded destination. Existing seat reservations still count towards capacity.
- Audit browsing/export omits excluded scopes. Unattributable legacy/unscoped signed events are also omitted for a person with exclusions rather than guessing their scope. Invalid-signature indicators remain visible without trusted context. Changes to exclusions/hierarchy invalidate that person's captured audit view; they must explicitly read a new view. Raw signed audit records are never rewritten.

Source and local synthetic tests do not verify the installed IIS/AD environment. SQL Server and PostgreSQL integration tests must pass in CI before merge; after deployment, verify the actual IIS runtime and effective database/certificate permissions.
