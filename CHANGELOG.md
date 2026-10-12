# Changelog

## 1.2.4 – 2026-10-12

- Save incomplete setup settings as an encrypted server-side draft from any step, without requiring connection or validation checks to pass.
- Load a saved draft using Windows sign-in and the setup code, restoring settings and the current step after a reload or restart.
- Save drafts automatically before database tests and full setup validation. Exclude all passwords and setup codes in both the browser and server; re-enter passwords when resuming.
- Keep drafts separate from active configuration: saving does not consume the setup code or complete installation. Finishing still requires successful checks and removes the draft.
- Add Swedish/English guidance and regression coverage for protection, authorization, password exclusion, reload/resume and failed checks.

Package: `Valvra-1.2.4-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are required. Preserve protected installation state, checkpoints and certificates; follow the in-place upgrade checklist.

## 1.2.3 – 2026-10-12

- Fix Active Directory setup failing before authentication at `LdapSessionOptions.Signing`. Windows rejects SASL signing/sealing options on an SSL session; LDAPS continues to require TLS and certificate validation with Negotiate authentication.
- Apply the same fix to approved LDAP credential tests through shared connection configuration, and dispose connections if configuration fails.
- Add Windows regression tests for service and explicit credentials that reproduce the original exception before the fix.

Package: `Valvra-1.2.3-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are required. Preserve protected installation state, checkpoints and certificates; follow the in-place upgrade checklist. After updating, retry the AD setup check against your domain controller.

## 1.2.2 – 2026-10-12

- Add an explicit SQL Server **Trust server certificate** option to setup, disabled by default. TLS encryption remains mandatory; enabling the option bypasses certificate chain and hostname validation.
- Add SQL Server **MultiSubnetFailover** and a bounded connection timeout (1–120 seconds, default 10) under advanced database connection options.
- Apply the selected options consistently to database tests, temporary schema-installation connections and saved runtime connections. Separate audit readers can use independent options; shared readers inherit the writer's settings.
- Include Swedish/English help, a review of the selected options, provider-switch handling and regression coverage. PostgreSQL continues to require full certificate validation.

Package: `Valvra-1.2.2-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are required. Existing installations retain their connection settings. Preserve protected installation state, checkpoints and certificates; follow the in-place upgrade checklist.

## 1.2.1 – 2026-10-12

- Update direct and transitive NuGet dependencies to current stable versions, including Npgsql, SQL Server client libraries, identity libraries, Serilog and test tooling.
- Centralize reviewed NuGet versions and enable transitive pinning in `Directory.Packages.props`. Retain the latest .NET 10-compatible MSBuild packages (18.9.6); 18.10.1 requires .NET 11. See [the dependency baseline](docs/DEPENDENCIES.md).
- Update Playwright and its matching Chromium, and pin current stable GitHub checkout and artifact actions by commit.
- Include npm in weekly Dependabot update checks alongside NuGet and GitHub Actions.

Package: `Valvra-1.2.1-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are required. Preserve protected installation state, checkpoints and certificates; follow the in-place upgrade checklist.

## 1.2.0 – 2026-10-12

- Add configurable Serilog JSON diagnostic files with daily/size rotation and bounded retention.
- Record lifecycle, setup checks, request timing/status and correlation IDs, with safe exception types, stack locations and database/LDAP error codes. Exclude secret-bearing messages and request data.
- Document log permissions, troubleshooting, configuration and sink failure behavior.
- Add a separate database connection test, expandable redacted setup errors, and targeted Active Directory troubleshooting guidance in Swedish and English.
- Update CI actions and test dependencies; assign import-test ports dynamically and bound preview process shutdown.

Package: `Valvra-1.2.0-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are required. Preserve protected installation files, integrity checkpoints and certificates. Diagnostic logging is enabled by default under `App_Data/Logs`; ensure the application pool identity can write there and review retention settings.

## 1.1.0 – 2026-10-06

- Add a password import wizard for UTF-8 CSV/TSV, with delimiter detection, editable column mappings and a preview that does not display secrets.
- Map group paths with up to two levels, or separate group/subgroup columns. Reuse visible existing groups/resources, map each source branch to a selected group, or import into one existing resource.
- Skip existing and repeated titles within the same resource without overwriting passwords. New objects inherit destination permissions; import does not grant access.
- Encrypt and audit each created record through the existing server operations. Clear staged values on focus loss, inactivity or cancellation; report partial completion and uncertain write responses without automatic retries.
- Include Swedish/English UI, mobile and accessibility checks, parser/planning regressions and an isolated import browser suite in CI.

Package: `Valvra-1.1.0-win-x64.zip` and `SHA256SUMS.txt`. No database schema changes or data conversion are required for an update from 1.0.x. Preserve protected installation files and certificates as described in the operations guide. Import supports passwords only, up to 2 MiB and 1,000 entries, and is not an all-or-nothing transaction. See [the import guide](docs/IMPORT.md).

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
