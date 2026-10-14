# UX review follow-up

Scope: seven findings from the independent Astra review of merged commit `49659e9`. Changes are reviewed, tested, committed and pushed in related batches. After these fixes, review complete administrator and ordinary-user journeys rather than treating this defect list as an application-wide UX acceptance test.

| Finding | State | Evidence |
| --- | --- | --- |
| Duplicate global-role display names | Implemented | Display name plus sAMAccountName, stable SID/provider submission. Independent review; 28 backend, 41 Node, 13 browser checks passed; required CI passed. |
| Password editing after focus loss replaces optional secret fields with blanks | Implemented | Preserve/Replace contract; metadata edits require no secret read; blur resets replacement to Preserve; explicit audited read for replacement. Fresh Modify authorization, revision checks and encrypted history retained. |
| Successful mutation acknowledgement lost after focus loss | Implemented | Separate dialog-instance and secret-read epochs; successful forms retire with safe acknowledgement; pending duplicate submissions blocked; old completion leaves newer dialogs intact. Independent review; 26 backend, 41 Node, 11 existing browser checks and three new safety checks passed after required-label correction. |
| Ambiguous license assignments and immediate reclaim/context loss | Implemented | License-scoped Modify-authorized user search, display/account names with stable-ID fallback, explicit full-path resource selection, reclaim confirmation and refreshed parent return. Review corrected inactive-fieldset validation. 16 backend, 41 Node, 20 existing accessibility checks and two assignment workflows passed. |
| License overview request fan-out and all-or-nothing failure | Implemented | Metadata-authorized aggregate pages of 200, sequential capped loading, marked partial results and resume-at-offset Retry. Review corrected absolute retry cap. Five backend, 22 broad browser checks, three loading checks and the corrected stale-response route regression passed. |
| License expiry date shifts during edit/save | Implemented | Local calendar date display; unchanged date preserves exact stored instant; changed dates use local end-of-day. 46 Node tests include four time zones and a DST boundary. |
| Audit export/paging move when new events arrive | Implemented | Immutable, actor/filter-bound server snapshots; bounded payload cache and five-minute expiry; exact snapshot paging/export with fresh auditor authorization and required export audit acknowledgement. 28 backend, 46 Node, 23 browser checks and seven audit workflows including new arrivals/expiry passed. |

## Combined verification

The combined implementation passed 272 backend tests (two external-database tests skipped locally), 46 Node checks, all 97 accessibility/browser workflows and all six import workflows. SQL Server/PostgreSQL checks passed in CI on implementation commit `4fb7c77`.

One of the two CI browser runs caught a race in the existing success-notice expiry test: it advanced the clock before awaiting the asynchronous acknowledgement, and did not remove the pointer from the message's intentional hover pause. Commit `fd43c68` corrects those test preconditions without changing application behavior; all 15 checks across five repetitions passed, with independent review. Required CI is rerunning on that correction.

This closes the seven-defect implementation checklist, not application-wide UX acceptance. The [journey review](UX-JOURNEY-REVIEW.md) identifies seven broader improvements across administrator and ordinary-user tasks, with explicit evidence limits and an implementation order. Its first slice clarifies administration/global-role scope, resource paths, capability wording and permission-aware empty states without adding permissions or endpoints.

Limits: synthetic tests do not establish production IIS/AD/SSO, real directory display/account names, the installation's original import failure, certificate rotation/restoration, or manual screen-reader behavior. Keep secret clearing and server authorization as invariants throughout the next journey review.
