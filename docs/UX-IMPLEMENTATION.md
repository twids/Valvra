# UX overhaul implementation

Scope: all 22 findings in [UX review](UX-REVIEW.md). Work is based on released 1.2.4. Unrelated changes in the original checkout remain separate. Each related batch is reviewed, tested, committed, and pushed before proceeding.

| Batch | Findings | State | Validation |
| --- | --- | --- | --- |
| Safe directory recipient selection | UX-03, UX-04, recipient-type part of UX-13 | Implemented | Independent security review: no blockers. Preview build, syntax checks, permission/settings browser tests. Explicit recipient POST and delayed-response regressions included. |
| Feedback and dialog infrastructure | UX-01, UX-18, shared pending/Close behavior | Pending | |
| Readable and understandable permissions | UX-02, UX-05, UX-06, UX-07, remainder UX-13 | Pending | |
| Administrator import and diagnostics | UX-08, UX-09 | Pending | |
| Administration navigation, version, certificates | UX-10, UX-20, UX-21 | Pending | |
| Group menus and moves | UX-11, UX-12 | Pending | |
| Resource browsing and mobile actions | UX-14, UX-15 | Pending | |
| License overview | UX-16 | Pending | |
| Audit and asynchronous view states | UX-17, UX-19 | Pending | |
| Secret/history navigation | UX-22 | Pending | |

Security invariants: fresh server authorization, least privilege, no content-right bypass for administrators, text-only identity/error rendering, CSRF protection, audit acknowledgement, no plaintext secrets in logs/storage/previews, and existing secret clearing. Rotation must preserve old verification/decryption material and recoverability.
