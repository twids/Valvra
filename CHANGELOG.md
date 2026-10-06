# Changelog

## 1.0.1 – 2026-10-06

- Show directory display names alongside stable user IDs in the global administrator list and role editor. Accounts that cannot be found still display their stable IDs and can have permissions revoked.
- Improve spacing in the directory settings form and global permissions section.
- Publish the installation, settings, security, operations and accessibility documentation in English while retaining Swedish and English in the application.
- Add regression coverage for resolving administrator display names. Existing person-bound permissions, bootstrap protection, encryption and audit remain in place.
- Fix operator key rotation and interrupted integrity recovery to authorize against person-bound vault roles, ignoring legacy directory administrator flags. Recovery checks the signed previous snapshot before reading the operator's role.

Package: `Valvra-1.0.1-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are included in this patch. Follow the operations guide for backups and an in-place update from 1.0.0; do not rerun the fresh-installation script over an existing website.

## 1.0.0 – 2026-10-06

The first regular release of Valvra for Windows/IIS, .NET 10 and AD/Windows SSO.

- Passwords and software licenses with encrypted version history, resource groups, inherited permissions and temporary read access.
- AES-256-GCM bound to the installation, resource, record and version, with a separate signed checkpoint for vault integrity.
- Signed audit records in a separate database, restricted database accounts and historical resource/group filters for lists and exports.
- SQL Server and PostgreSQL with separate migrations and installation checks. A shared Windows service identity for the SQL Server vault and audit is supported.
- Installation wizard, directory settings, global roles for individual user accounts and approved LDAP test profiles.
- Swedish and English, keyboard support, accessibility regressions, direct links and browser history.
- A separate read-only demo with synthetic data. Demo and test identities are not included in the IIS package.

Package: `Valvra-1.0.0-win-x64.zip` and `SHA256SUMS.txt`. Requires the .NET 10 Hosting Bundle, IIS Windows Authentication, trusted certificates and correctly configured databases/AD.

A fresh installation requires an empty vault. Conversion of earlier preview data is not included. Windows SSO, Extended Protection, dMSA/gMSA and backup/recovery must be verified in the target environment as described in the README and `docs/VERIFICATION.md`. Manual screen reader testing and an independent security review have not been performed; automated tests do not establish full WCAG conformance.
