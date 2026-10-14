# Journey UX implementation

Scope: all seven improvements in [the journey review](UX-JOURNEY-REVIEW.md). Related batches are reviewed, tested, committed and pushed separately. Production deployment and manual assistive-technology acceptance remain separate.

| Batch | Findings | State and evidence |
| --- | --- | --- |
| Navigation and capability clarity | J1/J3, breadcrumb portion of J2 | Administration versus global roles/resource access, section headings, capability summaries and permission-aware empty states. Independent review corrected license empty guidance. All 19 persona/navigation/settings browser workflows and 46 Node checks passed. No new roles or endpoints. |
| Find and edit content | J2/J7 | Metadata-only title/product filtering retained per resource, explicit replacement fields, assignment product/path/seat context. Independent review; 46 Node checks, 27 existing accessibility/history workflows, and all eight affected persona/assignment/mutation checks passed after correcting detached-label handling and a translated test label. |
| Guided access grants | J4 | Metadata-only default and explicit read/edit/custom presets, live recipient/account/path/rights/duration review, inherited-scope explanation and account names in existing access rows. Independent review; 27 browser workflows and 46 Node checks passed, including stable-ID and Modify-only submission. |
| Recoverable import | J5 | Pending |
| Understandable audit evidence | J6 | Pending |

Every batch retains fresh server authorization, stable identity submission, text-only rendering, CSRF, secret clearing, encrypted history and audit acknowledgement. Search uses already-authorized metadata only. Presets must not silently add permissions; audit presentation must preserve incomplete and invalid-signature records.
