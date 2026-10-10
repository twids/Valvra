# Verification and upgrade regressions

## Release 1.1.0 import verification

Verified locally on 2026-10-06: **202 C# tests**, **36 Node tests** and **4 isolated import Playwright/axe tests** passed. The import browser suite verifies two-level hierarchy creation and reuse, manual branch mapping, duplicate preservation, signed audit, focus-loss clearing, English UI, mobile layout and untrusted titles rendered as plain text. It runs separately from the existing accessibility suite so imported test records do not change that suite's fixed data.

Run `npm run test:import` for the isolated import suite; see [the import guide](IMPORT.md) for format, permission and partial-completion boundaries. The release CI must pass the existing accessibility, Windows and both-provider database jobs, including the new import suite. The two database contract tests are skipped locally without dedicated connections; CI verifies them in disposable containers. Production IIS/AD and manual accessibility checks retain the scope below.

## Automatically verified

Verified locally on 2026-10-05: **112 .NET tests in Release and 10 client tests passed**. The run included two contract tests against real SQL Server 2022 and PostgreSQL 17 in isolated, disposable Dockhand containers, plus Windows DPAPI tests. When running locally without test databases, only the two database tests are skipped.

The CI configuration runs both database providers on Linux and the remaining tests on Windows, collects Cobertura/TRX and publishes the IIS package. All three GitHub CI jobs passed for release 1.0.0. The sections below retain the historical scope and results of individual local runs.

Tests cover, among other things:

- AES-GCM, tampering with ciphertext/nonce/tag/data keys, binding to installation/resource/record/version and switching key protection.
- Direct SQL manipulation of permissions, owners, resource structure, passwords, licenses and the outbox: access denied before decryption, signing or delivery.
- Deleted/modified checkpoints, database rollback after restart, unexpected trigger changes and interruptions before/after checkpoint/commit, including limited operator recovery.
- Inherited permissions, administrators without automatic read access and exact expiration of temporary access.
- Audit outages before release/changes, redelivery after commit and lost commit acknowledgments.
- Migrations for both databases and actual SELECT+INSERT permissions without UPDATE/DELETE/TRUNCATE/ALTER for a shared audit account, plus optional INSERT-only/SELECT-only identities.
- HTTP rejection, authentication, CSRF, security headers and attempts to read another user's record by ID.
- Setup codes, DPAPI protection and connection strings for the service's Windows identity.
- Separate LDAP test permission, a single test request, time limits and permission checks after slow audit delivery.
- Version history, conflict protection, soft delete and license seats without license key decryption.
- License metadata without read permission, explicit preserve/replace/clear choices, encrypted license history and restoration with both read and modify permissions.
- Group depth 128/129 during creation and moves, removed owners, revoked permissions and changed inheritance after a resource move.
- Client loss of focus, inactivity and late release responses: sensitive fields are cleared, metadata drafts are retained and an empty replacement key cannot be saved.
- The separate demo shows only fixed synthetic data and rejects changes, setup and credential input. Its assembly does not reference vault/database libraries.

The resource view, password display, history, permission forms, license overview, audit and installation wizard have also been checked in a browser using synthetic data. The test server and test identities exist only in test projects and are not published in the IIS package.

License draft clearing has been checked on the actual page with simulated loss of focus. The running Dockhand demo returned HTTP 200 for the page/client script, 405 for mutations, 400 for credential input and 404 for setup. This demo is separate from production and uses no databases.

## Code coverage and .NET upgrades

Reports cover the Core, Infrastructure and Web production modules; tests, the demo and generated migrations are excluded. Critical source files have the following verified line coverage (asynchronous representations of the same source line are counted once):

| Source file | Line coverage |
| --- | ---: |
| EnvelopeCipher | 97.8% |
| VaultIntegrity | 94.0% |
| AccessService | 100.0% |
| AuditService | 95.1% |
| VaultOperations | 95.8% |
| Protected VaultService facade | 93.9% |

The database job fails if any of these files falls below 90%. Infrastructure overall is around 70% and Web around 48%; the table describes critical files, not full coverage of Windows/IIS/LDAP/certificate integration. Line coverage does not replace meaningful negative tests.

When upgrading, change `global.json`, target frameworks and package versions on a separate branch, restore packages and run:

```powershell
dotnet test Valvra.slnx -c Release --settings tests/coverage.runsettings --collect:"Code Coverage" --logger "trx;LogFileName=tests.trx" --results-directory artifacts/test-results
node --test tests/browser/*.test.cjs
.\tests\Assert-SecurityCoverage.ps1
dotnet publish src/Valvra.Web -c Release -r win-x64 --self-contained false -o artifacts/publish
```

Set `VALVRA_TEST_SQLSERVER` and `VALVRA_TEST_POSTGRESQL` to **dedicated disposable test servers** for both database contracts, or use CI's isolated databases. The tests create and delete their own randomly named databases and accounts. Production servers must not be used.

Frozen AES-GCM, JSON and hash vectors protect the existing format 2 against unintended serializer/culture/SDK changes. Model inventories and migration checks cover both providers. Do not change frozen expectations to make an upgrade pass without investigating why the storage contract changed. Also rehearse restoring a backup of the current format and perform the environment checks below with the new runtime package. A future .NET version is not verified until this run and the Windows/IIS/AD checks pass.

CI uses password authentication and TLS verification exceptions **only in its temporary databases**. Production configuration requires trusted certificates. CI therefore does not prove a particular organization's Kerberos, LDAPS or certificate configuration.

## Installation checks in your own environment

A real domain-joined IIS/AD installation has not been available in the development environment. Follow the README and let the setup wizard verify the service identity's database connections, directory, certificates and audit role permissions. Verify dMSA/gMSA separately in the prepared Windows/AD environment. Then check the following using test accounts and synthetic secrets:

1. Windows SSO from a client, current AD group membership and rejection of disabled accounts. Also perform and document [the README's mandatory Windows sign-in verification](../README.md#21-require-protection-for-windows-sign-in); successful setup does not replace this check.
2. A reader, a user with modify permission and a user without a grant; check both the interface and direct API requests.
3. Temporary access before and after expiration, plus signed audit records for reads and changes.
4. An approved LDAP test profile with a dedicated test account and the organization's account lockout policy.
5. Backup and actual restoration of both databases and cryptographic keys on an isolated server.

These checks are required for each installation environment; automated tests do not replace an independent security review before handling real organizational secrets.

## Resource and group filters in audit

Audit filter regressions cover historical group membership during resource/group moves, renaming, creation, deletion and failed changes; signed metadata without secret values; filtering with/without subgroups; identical filters in the API/list/export; and denied access without the auditor role. `AuditScopeTests` contains 17 new C# cases. The two provider contract tests have been extended with real SQL queries for both filtered searches and historical selectors. Browser tests are in `tests/accessibility/audit-filters.spec.cjs` and included in the regular accessibility job.

Verified locally on 2026-10-05: 127 C# tests, 10 client security tests and the two new Playwright/axe tests passed. Existing 90% security thresholds passed, including the new check for `AuditScopeCapture.cs` (36/36 covered lines). The two MSSQL/PostgreSQL contract tests were skipped because test connections were unavailable; their new SQL queries were therefore not verified against real databases in this run. The local SQLite-based test host does not verify provider-specific JSON queries or their performance. Run the provider contracts with separate test databases before production use.

For separate verification without overwriting a running preview's build output:

```powershell
dotnet build tests/Valvra.Preview -c Release -o artifacts/audit-filter-preview
npx playwright test --config tests/browser/audit-playwright.config.cjs
```

## Shared service account for audit

Local verification on 2026-10-05: 151 C# tests passed; two contract tests against real MSSQL/PostgreSQL servers were skipped because test connections were unavailable. 24 new setup cases cover shared Integrated Security without additional passwords, an optional reader account, preserved database separation, allowed minimum permissions and rejected destructive and unknown permissions. Two isolated Playwright/axe tests passed for the default installation and optional reader connection, including clearing its password when disabled.

Database contracts also include checks for actual SELECT+INSERT, denied UPDATE/DELETE/TRUNCATE/DROP and the setup wizard's permission queries for the shared account. These provider cases and actual dMSA sign-in were not run in this verification; check them before production use. Tested policy results do not replace actual SQL permission verification.

```powershell
dotnet test tests/Valvra.Tests -c Release -o artifacts/shared-audit-tests
dotnet build tests/Valvra.Preview -c Release -o artifacts/shared-audit-preview
npx playwright test --config tests/browser/setup-playwright.config.cjs
```

## Accessibility regressions

Basic WCAG checks and keyboard tests run separately with `npm ci`, `npx playwright install chromium` and `npm run test:accessibility`. See [the accessibility guide](ACCESSIBILITY.md) for criteria, scope and manual release checks.

Local verification on 2026-10-05: 13 Playwright/axe tests and 10 client security tests passed. In addition, 110 C# tests passed with `DatabaseContractTests` excluded; the two contract tests against real MSSQL/PostgreSQL databases were not rerun for this interface change. The accessibility job has been added to GitHub Actions but has not run there from this local working copy. This confirms selected interface checks, not full WCAG conformance or manual NVDA verification.

## Language regressions

Swedish and English share language files between client and server. See [the language guide](LOCALIZATION.md). Local language support verification on 2026-10-05: **168 C# tests**, **17 Node tests** and **24 Playwright/axe tests** passed. Contract tests against real MSSQL/PostgreSQL databases were not included in this run.

Language tests check catalog coverage, format parameters, language priority and fallback for invalid headers, persistent language selection, HTML language, form validation, installation drafts, passed installation checks, original user data values and unchanged CSRF and permission boundaries. English layout is also tested at 320 pixel width and 200% text size. Both languages still require manual screen reader testing before release. Only the isolated preview host exempts rate limiting so that the suite can make many requests with the same synthetic identity; production limits remain in place.

## Audit reader hardening after security review

Verified locally on 2026-10-05: **169 C# tests**, **20 Node tests** and **4 Playwright audit filter tests** passed. Existing 90% thresholds for security-critical files passed. Builds used already restored packages and a separate `--artifacts-path artifacts/audit-security-fix`; test logs, TRX and Cobertura are stored there.

`AuditReaderSecurityTests` uses the real verifier and read/selection logic with synthetic data rows. Regressions cover invalid representatives, tampered timestamp projections, malformed JSON, null/object group chains, oversized payloads, read limits, canceled reads, denied audit access and filtering/pagination after verification. Client and browser tests check that invalid context can be displayed safely and selector errors do not block the audit list. The client test also distinguishes identical filter choices by their exact IDs.

Both provider contracts have been extended with actual INSERT operations for corrupt audit rows and unsigned timestamp projections. **The two tests were skipped** because dedicated test connections were unavailable. Actual `GetChars` handling, SQL reads and performance on MSSQL/PostgreSQL therefore remain unverified for the fix; a test adapter does not replace a provider run. AuditDatabase.cs had 97/180 covered source lines in this run. Run both contracts and load-test representative history before production use. Read limits and corruption markers are documented in AUDIT.md.

## Resource group navigation

Local verification on 2026-10-05: **169 C# tests**, **24 Node tests** and the full suite of **35 Playwright/axe tests** passed. The C# run excluded the two `DatabaseContractTests`; real MSSQL/PostgreSQL databases were not included. Group navigation changes the client and uses existing API responses filtered by permissions.

`tests/browser/group-navigation.test.cjs` checks ID-based group scope, direct resources and subgroups, search, identical group names, missing parents, cycles and a 128-level hierarchy. The seven cases in `tests/accessibility/group-navigation.spec.cjs` test a navigable hierarchy, children and grandchildren, breadcrumbs, keyboard use, focus, retained filters, Swedish/English, default groups during creation, late metadata responses and layout at 320 pixel width. Automated axe checks are included; this does not replace manual screen reader testing.

The group view and its counts are based solely on metadata released by the server to the current user. Missing parents are not fetched separately, and the group view fetches no passwords or license keys. The tests' subgroups are synthetic metadata fixtures; opening resources uses the isolated preview host's real metadata endpoints. Permission checks and secret release continue to take place on the server.

## URLs and browser history

Verified locally on 2026-10-05: **193 C# tests**, **27 Node tests** and the full suite of **49 Playwright/axe tests** passed. The two `DatabaseContractTests` were excluded; real MSSQL/PostgreSQL databases and domain-joined IIS/AD were not included. Existing 90% thresholds for security-critical code passed with the report in `artifacts/routing-coverage`; `VaultIntegrity.cs` had 94.1% source line coverage.

The three contract tests in `tests/browser/navigation.test.cjs` test views, stable IDs, invalid addresses and unsafe route parameters. The ten cases in `tests/accessibility/routing.spec.cjs` test the full navigation chain, including settings, Back/Forward, direct links, reloads, early clicks during identity verification, filters and audit pages, ordinary opening in a new tab, Swedish/English UI regressions in the shared suite and denied links without content requests. They verify that secret dialogs are cleared without being replayed and that late responses do not change another view. Separate HTTP tests require authentication on all UI routes and check that API and static file addresses are not rewritten to HTML. Routes and user behavior are documented in [the navigation guide](NAVIGATION.md).

`IntegrityCancellationTests` reproduced two failures before the fix: cancellation after checkpoint preparation stopped commit, and a canceled audit record left a prepared checkpoint. All three cases pass after the fix: cancellation before preparation rolls back, cancellation after preparation completes exactly the approved change, and signed audit records can be delivered on the next request. Existing tampering, rollback and explicit operator recovery tests continued to pass. The new cancellation logic is verified with SQLite and synthetic checkpoint storage; provider-specific network/commit failures still require testing against real test databases.

## Release 1.0.0

Verified locally on 2026-10-06 with version 1.0.0: **194 C# tests**, **27 Node tests** and **49 Playwright/axe tests** passed. The two database provider contract tests were skipped locally because test connections were unavailable; they run in GitHub Actions' isolated SQL Server/PostgreSQL environment before publication. All seven security-critical files passed the 90% threshold. Local TRX/coverage reports and browser reports are under `artifacts/release-1.0.0`.

The Windows/IIS package was built for `win-x64` with `--self-contained false`. Version numbers, installation scripts, SQL scripts and documentation were checked, as was the absence of the demo, test identities, SQLite and protected installation material. Browser test preparation now builds both preview and demo so that the suite works from a clean working copy. The local run used separate build directories to preserve an already running preview.

Domain-joined IIS/AD, Extended Protection, dMSA/gMSA, recovery in the target environment and manual screen reader testing are not included in this automated verification. These scope limits also apply to release 1.0.0 as described in the README and accessibility guide.
## Release 1.0.1

Verified locally on 2026-10-06: **202 C# tests**, **27 Node tests** and **49 Playwright/axe tests** passed. The two real database contract tests were excluded locally; publication requires the GitHub CI database, Windows and accessibility jobs to pass for the release commit. Reports are under `artifacts/release-1.0.1`.

All seven security-critical files passed the 90% line coverage floor: EnvelopeCipher 97.8%, VaultIntegrity 94.3%, AccessService 100.0%, AuditService 95.6%, AuditScopeCapture 100.0%, VaultService 95.8% and ProtectedVaultService 93.9%.

Recovery tests now reject directory-only administrator claims, group roles, another identity provider, disabled accounts, temporary roles, another installation, a System Administrator without Access Administrator and tampered role storage. Recovery succeeds using the permanent person-bound role from the verified previous snapshot even when the directory's legacy administrator flag is false. The SQL Server/PostgreSQL contracts also exercise this recovery authorization query. Operator key rotation resolves the same person-bound roles while holding the verified integrity scope.

The package is built from the release commit for Windows/IIS, with SHA-256 checksums and English installation/operations documentation. It excludes demo/test identities, SQLite, private deployment notes and protected installation material. This patch requires no database schema change. The domain-specific and manual verification limits documented above continue to apply.
