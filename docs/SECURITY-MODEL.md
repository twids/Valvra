# Security model

Valvra is a server-controlled vault. The server may decrypt after checking permissions. An attacker controlling the server or the service identity with private key access can read secrets; this is not a zero-knowledge solution.

## Encryption

AES-256-GCM with a new random data key for every secret version. Authenticated additional data binds the content to the installation ID, resource ID, record ID, version, format and content type. Format 2 does not accept earlier unbound envelopes. Data keys are protected with RSA-OAEP/SHA-256 through an RSA certificate of at least 3072 bits outside the database. Titles, resource names and organizational structure are visible metadata. Account names, passwords, license keys and secret notes are encrypted.

Data key and plaintext byte buffers are zeroed after cryptographic operations. .NET strings, JSON binding and browser memory cannot be guaranteed to be erased. Secrets must not appear in logs, URLs, persistent browser storage or telemetry. The UI clears revealed secrets after 30 seconds or when the window loses focus. The web app cannot reliably revoke clipboard contents.

Loss of focus also clears sensitive edit fields. Ordinary form information is retained. Saving license metadata requires no decryption and preserves the existing key. Replacement requires a non-empty new key; clearing requires an explicit choice. Every key change creates an encrypted history version. Restoration requires both read and modify permissions. Late release responses after loss of focus or a dialog change are neither displayed nor copied to the clipboard.

## Integrity against a database attacker

GCM protects ciphertext but cannot, by itself, protect which users are granted access to which resources. The service therefore verifies the entire vault state before permission decisions and operations. The checkpoint contains the installation ID, generation and a SHA-256 hash of all ten domain tables, including owners, permissions, versions and the audit outbox. It is signed with a third, separate RSA-PSS certificate and stored with DPAPI protection in `App_Data` under a restricted NTFS ACL.

A database identity must not have access to checkpoint files or the service's private keys. This is a prerequisite: server administrators and the service identity are trusted. Protection detects database changes, deletions, additions and restoration of older vault data; it does not prevent a database administrator from causing an outage.

A file lock shared across processes and a serializable database transaction keep verification and the operation together. Only intended, EF-tracked changes may create the next signed checkpoint; unexpected trigger changes are not approved. The checkpoint is prepared before commit and completed afterward. Following an interruption, only the exact prepared new state is accepted automatically. The old state requires explicit operator recovery; an unknown state is rejected. See [the operations guide](OPERATIONS.md).

Client cancellation is honored before preparation. From preparation through completed commit, an internal 30-second time limit is used independently of the client connection to avoid a partially completed checkpoint, for example during a quick reload. This can complete an already verified change after the client has left the page. Storage or commit failures do not weaken integrity checks or recovery requirements.

The first implementation reads the entire state during every verification and protected save, and serializes operations. Data volume and a growing outbox therefore affect response time and memory. Run one worker process on one server; support for distributed instances and scaling is not verified. Load-test representative data volumes before production use. Automatic rebasing or automatic checkpoint removal is prohibited.

## Permissions

All checks take place on the server. The AD account and group memberships are retrieved before API operations; an AD outage results in denied access. Grants are combined from the resource and its group tree. There are no explicit deny rules or broken inheritance. Read, modify, LDAP test and administrator permissions are separate. Resource owners and central access administrators are trusted and can grant themselves read permission.

Temporary read access has exact start and end times in UTC and is checked on release. Values already released cannot be revoked. If necessary, change the password in the target system.

Global permissions are integrity-protected grants to individual accounts. AD groups and directory provider flags grant no global permissions. The installer receives access and system administration during protected setup, without automatic audit or secret read access. System administration is highly trusted because the role controls directory verification of identities. See [SETTINGS.md](SETTINGS.md) for roles, search bases, module boundaries and the audit limitation when saving configuration.

Group trees may have at most 128 levels. Both creation and moves check the depth of the entire subtree before making changes, so a delegated administrator cannot create a tree that makes vault listing unusable.

## Sign-in and operations

Windows SSO without additional MFA is the chosen sign-in flow. It relies on protection of the domain and clients. Databases use the service identity; no user delegation to SQL occurs. The server requires HTTPS and validated database/LDAPS certificates. Enable IIS Windows Authentication and disable Anonymous Authentication.

Also follow the mandatory step [Require protection for Windows sign-in](../README.md#21-require-protection-for-windows-sign-in). The installation script and setup wizard do not yet verify this protection automatically.

Installation requires Windows SSO and a random 256-bit one-time code generated by a server operator. Configuration is protected by DPAPI LocalMachine **and NTFS ACLs**: DPAPI does not replace file permissions. There is no local password sign-in, simulated user or secret web backdoor.

## Audit

Audit runs in the same service but in a separate database. The default installation uses the same service account with SELECT and INSERT, without modification, deletion or administration permissions in the audit database; separate writer/reader identities are optional. The application's auditor role is still required to access history. Events are signed. Limitations and protection against tampering/deletion are documented in [AUDIT.md](AUDIT.md). Individual signatures do not prove that the history is complete.
