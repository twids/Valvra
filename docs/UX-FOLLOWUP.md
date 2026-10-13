# UX review follow-up

Scope: seven findings from the independent Astra review of merged commit `49659e9`. Changes are reviewed, tested, committed and pushed in related batches. After these fixes, review complete administrator and ordinary-user journeys rather than treating this defect list as an application-wide UX acceptance test.

| Finding | State | Evidence |
| --- | --- | --- |
| Duplicate global-role display names | Implemented | Display name plus sAMAccountName, stable SID/provider submission. Independent review; 28 backend, 41 Node, 13 browser checks passed; required CI passed. |
| Password editing after focus loss replaces optional secret fields with blanks | Implemented | Preserve/Replace contract; metadata edits require no secret read; blur resets replacement to Preserve; explicit audited read for replacement. Fresh Modify authorization, revision checks and encrypted history retained. |
| Successful mutation acknowledgement lost after focus loss | Implemented | Separate dialog-instance and secret-read epochs; successful forms retire with safe acknowledgement; pending duplicate submissions blocked; old completion leaves newer dialogs intact. Independent review; 26 backend, 41 Node, 11 existing browser checks and three new safety checks passed after required-label correction. |
| Ambiguous license assignments and immediate reclaim/context loss | Pending | |
| License overview request fan-out and all-or-nothing failure | Pending | |
| License expiry date shifts during edit/save | Pending | |
| Audit export/paging move when new events arrive | Pending | |

Limits: synthetic tests do not establish production IIS/AD/SSO, real directory display/account names, the installation's original import failure, certificate rotation/restoration, or manual screen-reader behavior. Keep secret clearing and server authorization as invariants throughout the next journey review.
