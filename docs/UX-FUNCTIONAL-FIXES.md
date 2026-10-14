# Functional UX follow-up

The user confirmed that v1.3.0's missing Add actions in IIS were caused by missing content permissions; hiding those actions made the prerequisite unclear. The remaining requested work is tracked below. Existing installations/accounts are not modified by this source work.

| Work | State |
| --- | --- |
| Discoverable disabled Add actions with exact prerequisites; independent content-read failures | Implemented; browser persona/loading regressions passed |
| Edit existing direct grants without replacing recipients; preserve exact bits, schedules and revisions | Implemented; audit/authorization/revision and unchanged-interval tests passed |
| Readable version creators with recorded IDs retained | Implemented; metadata authorization and directory-outage fallback tested |
| Multi-level resource-group inheritance | Verified with real database ancestor traversal, create and revoke regression |
| License creation/deletion/recovery flow and form spacing | Implemented; dedicated creation form, resource/seat deletion context, recovery feedback, padded settings panels; 24 browser and 46 Node tests passed |
| Global Administrator with explicit per-person subtree/resource exclusions and explicit restoration | Implemented; authorization/restoration/integrity/audit tests passed; provider migrations require CI verification |
| Global License Administrator for license metadata/modification/assignments without implicit key reads | Implemented; license-only authorization and no-key-read regressions passed |

No resource content-type restriction is planned: the user explicitly declined it. New global roles will not be assigned to existing accounts automatically. The new role/exclusion model requires explicit authorization, audit, integrity protection, and SQL Server/PostgreSQL upgrade validation before delivery.

The global-role batch passed 284 backend tests locally (two real-provider tests require CI), 46 Node tests, all 111 browser cases across the initial run and 39-case affected-suite rerun, and 8 end-to-end import tests. Critical security coverage remains above 90%. See GLOBAL-ROLES-UPGRADE.md for schema-upgrade instructions. Identity presentation and prepopulated active-person lists are the next requested batch.
