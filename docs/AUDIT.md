# Audit: same service, separate database

Valvra writes audit records directly from the web application's .NET process to a **separate database**. No separate audit server, HTTP receiver or background service needs to be installed. Valvra's regular database and the audit database may reside on the same database server. A shared service account can be used, with different permissions in each database.

## One service account, two databases

| Connection | Purpose | Runtime permission |
| --- | --- | --- |
| Valvra | Vault, resources, grants and transactional audit outbox | Read/write on Valvra's tables; no schema administration |
| Audit writer | Insert signed audit events | `INSERT` on the audit table |
| Audit reader | Audit view, filtering and export | `SELECT` on the audit table |

The default installation uses **the same service identity and connection string** for both audit connections. Grant the service account the `valvra_audit_runtime` role: `SELECT` and `INSERT` on the audit table, without `UPDATE`, `DELETE`, `TRUNCATE`, `ALTER`, ownership or the ability to alter permissions. The account must not have other roles, column permissions or access to administrative procedures that can modify, delete or replace audit records. The audit transport code uses only INSERT even when the account also has read permission.

For MSSQL with Windows Integrated Security, IIS can run as **one dMSA or gMSA account**. Windows/AD manages account authentication; Valvra does not need to store the service account password or perform a separate Windows logon for audit reads. See [the database guide](DATABASES.md) for installation and environment requirements. When configuring connections manually, use the same value in `AuditDatabase:WriterConnectionString` and `AuditDatabase:ReaderConnectionString`; leave `ReaderWindowsCredentials` empty.

Separate identities remain optional: `valvra_audit_writer` receives only INSERT and `valvra_audit_reader` only SELECT. Enable **Use a separate audit reader (optional)** in setup. Do not put a shared account in these two MSSQL roles: their explicit DENY permissions block reads and inserts respectively. Instead, use only `valvra_audit_runtime` in the audit database.

The migration/installation identity must never be used as the runtime identity. Do not use `db_owner`, `sysadmin`, table/schema owners or a PostgreSQL superuser. The installation wizard tests inserts and reads and checks effective table and column permissions and certain administrative permissions. Unknown check results stop installation. The DBA must also review broader roles, permission administration and access through procedures; setup is not a complete inventory of all privilege paths.

Installation scripts: [MSSQL](../deploy/audit-sqlserver.sql) and [PostgreSQL](../deploy/audit-postgresql.sql). Run them as DBA in the audit database. Preserve the defined primary key and do not add other unique indexes without changing redelivery handling.

## Delivery and outages

Each event receives a UUID, UTC timestamp, the actor's stable provider/ID, target ID, operation, outcome and correlation ID. Passwords, license keys, account passwords, secret notes and cryptographic keys must never appear in audit records.

For secret release, the audit database must acknowledge the durable INSERT operation before decryption and response. `ReleaseAuthorized` means the server approved release, rather than proof that the client actually received or read the secret.

For changes, an intent event is written first. The change and result event are then committed in a local transaction in Valvra's database. The result is delivered to the audit database before success is reported to the client. The databases do not use a distributed transaction.

If the change has been committed and the result audit subsequently fails, the change may already have been applied despite the error message. The durable outbox redelivers the same event. The client must reload and check the current record before a manual retry. Mutations must not be resent automatically.

If the commit acknowledgment itself is lost, `CommitUncertain` is written instead of claiming that the change failed. Check the current record and any durable `Committed` event with the same operation ID. A connection failure during commit can mean either a completed or rolled-back transaction.

Identical redeliveries are deduplicated through `(EventId, PayloadHash)`, without the writer reading the table. Different content with the same event ID becomes separate, visible records; it must not be overwritten or hidden. Normal delivery reuses the same outbox event and content. An unavailable audit database blocks new protected operations. The outbox is delivery status, not the authoritative history.

## Filter by resource and resource group

The audit view has separate selectors for **Resource** and **Resource group**, plus **Include subgroups** (enabled by default). They can be combined with date, actor and operation. All selections are combined with AND. A group without subgroups shows the group's own events and its directly associated resources. With subgroups enabled, historical descendant groups and their resources are included. Export this page uses the same filters and page position as the list, up to 200 events.

The service captures the resource ID/name and the group's entire chain of IDs/names when the event is created. This metadata is included in the signed event (format 3) before it is stored in the outbox; passwords and license keys are never included. Password, license, LDAP test, permission and ownership events are associated with their resource or group. A move's intent is recorded in the source group and its completion in the destination group. Earlier events are never changed by moves, renaming or deletion. Failed changes retain the intent's original context.

Selectable objects are retrieved from verified, signed historical events through `GET /api/audit/targets`. The most recently recorded name for the same ID is used in the selector; each row and export retains the name at the time of the event. Deleted objects remain selectable. The auditor role is required for lists, filter options and exports; filtering grants no read or modify permission to the vault itself. An auditor can therefore filter history even without resource permissions.

API parameters: `resourceId`, `groupId` and `includeSubgroups=true|false` for both `GET /api/audit` and `GET /api/audit/export`. The group filter uses the signed historical group chain; there is no join against the current vault structure. Database requests are parameterized SELECT queries on the audit reader connection. The audit transport continues to use only INSERT, and audit continues to run in the same service with a separate database. No new database columns are needed. SQL reads do not interpret JSON or select through unsigned search projections. The service verifies candidates before filtering, deduplication and pagination; the latest name and sorting are determined by the signed timestamp and event ID. An invalid representative therefore cannot displace valid history. Corrupt or oversized payloads are shown as `Audit.InvalidPayload` with an invalid signature and no untrusted metadata. Such rows are shown even when filtering, because their scope cannot be verified. The selector reports `invalidEventCount` and uses only valid events. If the selector fails, the interface can still fetch the audit list.

Reads verify the entire candidate set for each request. They are deliberately bounded: `AuditDatabase:MaximumReadEvents` defaults to 100,000 (maximum 1,000,000), `MaximumReadBytes` defaults to 64 MiB counted as UTF-16 payload, and each individual payload may contain at most 262,144 characters before it is marked invalid. `CommandTimeoutSeconds` limits both database work and the verification loop. Exceeding total limits produces an explicit error, never a silently truncated result. Load-test representative history; larger installations need a separate trusted indexing/archiving solution before these limits can be handled efficiently.

Format 2 can still be verified byte for byte. Older events without historical context, and service-wide events such as identity checks/exports, are shown in unfiltered searches but excluded from resource/group filters. There is no reconstruction from the current structure or re-signing of older data.

## Signing and protection limits

The event's entire serialized content is hashed with SHA-256 and signed using RSA-PSS/SHA-256 and a separate audit certificate, at least RSA-3072. The private key resides in the Windows certificate store with access restricted to the service identity. Old public certificates are retained for verification after rotation. The vault encryption certificate and audit certificate must differ.

Signing occurs **when the service creates the event, before it is stored in the vault outbox**. Delivery verifies the already signed exact payload bytes, installation ID and signature. The transport never re-signs content read from the database. An edited outbox must therefore not be converted into trusted audit records. Outbox projections and delivery status are also covered by the vault's external integrity checkpoint. Tampering or deletion stops protected operations before signing, decryption or delivery.

The audit view verifies the signature of each returned event. A database administrator without the signing key cannot change signed content without verification failing. Search index columns are only projections; the signed event is the basis for verification.

**INSERT-only does not make history impossible to delete.** A DBA can delete records, restore an older backup or alter permissions. Signing individual records does not prove that no records are missing. An attacker controlling the application's service identity and signing key can create validly signed false events. Stronger evidence against deletion requires separately protected backups or external/WORM checkpoints. This architecture does not guarantee that protection.

No automatic retention cleanup is performed. The organization must establish retention periods, access, backup and any personal data handling before production use. Valvra's runtime accounts must not receive DELETE permission for retention cleanup; such handling is a separate administrative process.
