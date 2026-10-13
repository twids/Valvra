# UX overhaul implementation

Scope: all 22 findings in [UX review](UX-REVIEW.md). Work is based on released 1.2.4. Unrelated changes in the original checkout remain separate. Each related batch is reviewed, tested, committed, and pushed before proceeding.

| Batch | Findings | State | Validation |
| --- | --- | --- | --- |
| Safe directory recipient selection | UX-03, UX-04, recipient-type part of UX-13 | Implemented | Independent security review: no blockers. Preview build, syntax checks, permission/settings browser tests. Explicit recipient POST and delayed-response regressions included. |
| Feedback and dialog infrastructure | UX-01, UX-18, shared pending/Close behavior | Implemented | Independent review findings corrected; 27 browser checks passed and 11 affected checks rerun after corrections. Dismissible/expiring success, persistent errors, text-only rendering, scoped table names, duplicate action guard. |
| Readable and understandable permissions | UX-02, UX-05, UX-06, UX-07, remainder UX-13 | Implemented | Security review correction tested with native LDAP failures. Backend: 250 passed, two external provider tests skipped. Six focused browser checks passed. Display-only directory lookup has a count cap and cooperative time budget; synchronous LDAP may exceed that budget. Unresolved identities retained; effective caller rights, source links, confirmation, parent return, and interval validation. |
| Administrator import and diagnostics | UX-08, UX-09 | Implemented | Four backend security tests, 13 Node tests, six full import browser workflows passed. Independent review corrected denial auditing and old/new import lifecycle races. Authorized bounded server batches, content rights retained, duplicates skipped server-side, safe partial diagnostics, only pre-processing 429 retried; cancel/focus-loss stops subsequent batches. |
| Administration navigation, version, certificates | UX-10, UX-20, UX-21 | Implemented | Two backend tests; 41 Node tests; 11 browser checks plus seven settings checks rerun after reviewed draft-race fixes. Routed role-aware sections, people first, retained draft, runtime version/footer, authorized certificate metadata and operator runbook. No key-mutating browser endpoint. |
| Group menus and moves | UX-11, UX-12 | Implemented | Independent review corrected native menu reopening. Nine group/browser checks, 41 Node tests and four backend administration tests passed. Anchored menus preserve layout and keyboard focus; full paths/IDs and added/removed/retained access previews; invalid descendants excluded. |
| Resource browsing and mobile actions | UX-14, UX-15 | Pending | |
| License overview | UX-16 | Pending | |
| Audit and asynchronous view states | UX-17, UX-19 | Pending | |
| Secret/history navigation | UX-22 | Pending | |

Security invariants: fresh server authorization, least privilege, no content-right bypass for administrators, text-only identity/error rendering, CSRF protection, audit acknowledgement, no plaintext secrets in logs/storage/previews, and existing secret clearing. Rotation must preserve old verification/decryption material and recoverability.
