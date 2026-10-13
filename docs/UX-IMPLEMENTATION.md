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
| Resource browsing and mobile actions | UX-14, UX-15 | Implemented | Independent review: no blockers. 41 Node tests; 53 broad browser checks passed initially, and all 33 affected checks passed after correcting delayed-scroll menu dismissal and keyboard traversal. Visible counts, clear filters, sortable compact list/cards with history preservation, collapsible hierarchy, primary Show/Copy and secondary menus, stacked mobile records. |
| License overview | UX-16 | Implemented | Independent review: no blockers; 41 Node tests and 11 browser checks passed. Search, sorting and actionable summaries use metadata only; Add explains missing destination rights; explicit full-path/ID destination selection, with server authorization unchanged. |
| Audit and asynchronous view states | UX-17, UX-19 | Implemented | Independent review caught and corrected unavailable-route busy-state race. 29 backend security checks, 41 Node tests, 19 browser checks plus five loading checks after correction passed. Verified-history actor/operation choices, provider-aware actor filter, applied query bound to export/paging, boundary/busy controls, explicit read-only retry and stale-response protection. |
| Secret/history navigation | UX-22 | Implemented | Independent review caught and corrected shared-dialog busy cleanup. 41 Node tests; 31 browser checks and seven history checks after correction passed. Visible remaining time, fresh metadata-only Back/escape return, restore-confirmation return, history loading/retry, blur invalidation; timeout and focus loss close without re-releasing content. |

Security invariants: fresh server authorization, least privilege, no content-right bypass for administrators, text-only identity/error rendering, CSRF protection, audit acknowledgement, no plaintext secrets in logs/storage/previews, and existing secret clearing. Rotation must preserve old verification/decryption material and recoverability.

## Combined verification

All 22 findings have implemented changes. PR #16 contains UX-01 through UX-10, UX-13, UX-18, UX-20 and UX-21. The follow-up branch contains UX-11, UX-12, UX-14 through UX-17, UX-19 and UX-22, with one reviewed commit per related batch.

Final local verification: 262 backend tests passed, two external SQL Server/PostgreSQL tests skipped because provider services were not configured; 41 Node tests passed; all 86 browser/accessibility checks passed. Five audit checks were rerun after correcting the final actor placeholder. Independent reviews found and verified corrections for menu scroll/focus, unavailable-view busy cleanup and shared-dialog busy cleanup.

Hands-on checks against an isolated synthetic preview verified compact browsing, the license overview, a 390-pixel mobile resource view, recorded-actor/operation filtering, and no browser console errors. Visual inspection led to grouping toolbar labels with their controls so wrapping cannot separate them. Screenshots contain synthetic data only:

- [License overview](ux-review/completed-license-overview.png)
- [Mobile resource](ux-review/completed-mobile-resource.png)
- [Applied audit filters](ux-review/completed-audit-filters.png)

These results do not establish production IIS/AD/SSO behavior, the installation's original import failure, actual certificate private-key access/rotation or backup restoration, or manual screen-reader usability. Certificate rotation remains an operator workflow with role-restricted status and guidance in the UI. Deployment and the follow-up PR merge are separate from implementation completion.
