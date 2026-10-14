# Valvra journey review: administrator, reader and editor

Reviewed 2026-10-13–14 against application code at commit `4fb7c77`, after the seven fixes in [UX-FOLLOWUP.md](UX-FOLLOWUP.md). Final checkout verification found `fd43c68`; the only intervening tracked change was the feedback browser test, so the application evidence remains applicable.

## Verdict and evidence

The application now has substantially safer interaction contracts. The next overhaul should make complete tasks easier to understand and finish, rather than add another layer of isolated controls. The main remaining friction is the distinction between content and administration, technical permission/audit language, and forms that expose more machinery than the current task needs.

**Observed UI:** the independent reviewer used the current synthetic preview at `http://localhost:58905` through CUA in an owned Chrome tab. Journeys inspected were resource discovery/detail; global-role search using Enter; permission overview, inherited sources, Add and Back; import discovery and its first step; license assignment overview, Add and Back; audit operation filtering; password metadata editing; and certificate status/guidance. No grant, global role, credential, certificate, or directory configuration was changed. No secret was copied to the clipboard. A desktop certificate-page screenshot was visually inspected, but no screenshot artifact was saved.

**Source-backed only:** reader/editor authorization-specific views, import mapping/preview recovery, and empty states. The initial CUA backend rejected the per-tab test-header command. The parent subsequently prepared reader/editor loopback proxies on 58906/58907, but after the session interruption CUA reported an empty browser inventory. The reviewer therefore did not complete live reader/editor sessions through those proxies. The editor form was inspected live under the administrator identity; that is not proof of the editor identity's end-to-end workflow.

**Not established:** manual screen-reader usability, a new mobile viewport pass, real directory identities/SSO, production import, certificate rotation, or recovery. Existing automated checks are useful but do not substitute for these journeys. This is a product-design review, not a claim of additional security vulnerabilities or a re-review of the seven fixed defects.

## What works and should remain

- Resource cards have clear open actions; Show and Copy are primary, with secondary actions in stable menus. Routes and headings retain useful navigation context.
- The permission overview explains direct versus inherited grants. Add and Back returned to the parent overview in the live review. Role search Enter performed a search without submitting a grant.
- Import is discoverable from administration and authorized resource contexts. Its administrator-plus-destination-rights boundary is explicit.
- License assignments now have a readable overview, explicit destination selection, a return path and confirmation for reclaim. Add and Back worked in the inspected UI.
- Existing-password editing defaults to preserving secrets; the inspected form did not automatically reveal saved values. This is an important improvement to retain while simplifying the form.
- Certificate guidance correctly separates browser-visible metadata from untested private-key access and operator rotation. Keep that boundary.
- Audit filtering now operates on a stable bounded snapshot, with explicit refresh/export behavior. Keep fresh server authorization, verified-event handling and export acknowledgement.

## Role/task matrix

| Persona | Primary task | Appropriate surface and boundary | Evidence in this review |
| --- | --- | --- | --- |
| Reader: Metadata + Read | Find account/license; reveal/copy; inspect history | Resource metadata and Show/Copy; no edit, access administration or import | Shared resource UI observed as administrator; reader-specific gating inspected in source only |
| Editor: Metadata + Read + Modify | Add content, change metadata, deliberately replace secrets, manage license seats | Content/assignment actions; no implicit ManageAccess or global role | Editing/assignment forms observed as administrator; scoped editor authorization inspected in source only |
| Access administrator without content rights | Create structure and manage access/global roles | Administration and access controls; no automatic secret read/write; import still needs destination rights | Administration UI observed with seeded administrator that also had content rights; no-content variant not exercised live |
| System administrator | Maintain directory and inspect certificate guidance | Operational sections; no automatic resource content rights | Settings/certificate UI observed under combined-role administrator; isolated-role journey not exercised live |
| Auditor | Investigate events and export exact viewed evidence | Human-readable filters with technical evidence details; fresh authorization and export audit acknowledgement | Filter/results/snapshot text observed under combined-role administrator; download and isolated auditor role not exercised |

## Prioritized journey improvements

### J1 · P2 · Make access administration's scope unmistakable

**Observed:** Settings → “Personer och behörigheter” opens a table containing only global roles. Resource grants are managed elsewhere from each resource/group. The overall settings heading remains “Inställningar” and the same directory-oriented description on People, Import and Certificates. An administrator looking to give someone access to one password can plausibly begin in the global-role area.

**Change:** Name the main area “Administration”; distinguish “Globala roller” from “Åtkomst till resurser”. Explain the boundary in one sentence and link to the resource/group task. Use the active section as the page heading and browser title. This does not require exposing all users' access in a new global endpoint.

**Acceptance:** An administrator can identify where to grant resource access without trying a global role. A system administrator sees only operational sections allowed by their role. Ordinary users retain no administration controls.

**Evidence:** `src/Valvra.Web/wwwroot/js/settings.js:9–14,106–122`; `src/Valvra.Web/wwwroot/js/app.js:429`.

### J2 · P2 · Give ordinary users a clear find-and-use journey

**Observed/source:** The landing search is explicitly a resource-name search; it does not search credential titles. Inside a resource, every password is rendered with no local content filter. Opening a resource through `/resources/...` also omits the group breadcrumb present when arriving through `/groups/...`. A user who knows an account title but not its resource must explore resources individually, and loses group context on the common route.

**Change:** Always show the resource's group path. Add a lightweight “Sök i resursen” filter over authorized metadata titles/products, with counts and a clear empty state. Consider a server-authorized cross-resource title search as a later increment only if real data volumes justify it; do not search usernames, notes, passwords or license keys by decrypting them.

**Acceptance:** A reader can locate an account within a resource, use Show/Copy, and return to the previous filter. Duplicate resource names are distinguishable by path. Search never returns metadata outside current permission scope.

**Evidence:** `app.js:246–266,305–307,424–453`. Resource/detail navigation observed live; large-list and reader identity consequences are source-backed.

### J3 · P2 · Explain “what I can do” without requiring permission terminology

**Observed/source:** Resource headers list “Metadata, Läsa, Ändra, Behörigheter, LDAP-test”. The full permission dialog is available only to managers. An ordinary reader cannot inspect an explanation of their own rights or upcoming temporary-access expiry there. Empty password lists tell all users to add an account, even when the caller lacks Modify and no Add action exists.

**Change:** Use a short capability summary such as “Du kan visa och kopiera” / “Du kan även lägga till och ändra”. Add a narrowly scoped “Min åtkomst” explanation if expiry/source information is supplied by a caller-only authorized endpoint; do not expose the full grant list. Make empty states depend on the caller's actual available actions and provide a useful contact/admin route where available.

**Acceptance:** Reader empty states never instruct an impossible action. Readers understand why editing is absent. Temporary access, if shown, describes future release restrictions without implying copied secrets can be revoked.

**Evidence:** `app.js:429–440,601–620`; live technical header and manager overview; ordinary-user behavior source-backed.

### J4 · P2 · Guide grants by task and keep the consequences visible

**Observed:** The grant form presents recipient type, directory search, result selection, validity and four independent permission checkboxes at once. Metadata is implicit; “Hantera behörigheter” appears alongside ordinary content rights. The current form is safer, but still asks administrators to translate a real task into permission combinations.

**Change:** Offer optional, explicitly described starting choices such as “Endast se listan”, “Läsa och kopiera”, and “Läsa och ändra”, plus “Anpassade rättigheter”. Show the exact resulting rights, recipient/account name, scope and duration in a compact review summary beside the final Grant action. Carry readable account-name disambiguation into existing access rows too, with SID/provider in identity details. Keep ManageAccess and LDAP testing deliberate advanced choices. Preserve custom Modify-only and other supported least-privilege combinations; presets must not silently broaden existing rights.

**Acceptance:** The user can explain who receives what and on which group/resource before submitting. Metadata-only is a positive choice. Temporary access stays user-only and input validation remains both client- and server-side.

**Evidence:** live grant form; `app.js:631–648`.

### J5 · P2 · Turn import into a recoverable guided task

**Observed/source:** The first step is discoverable and states limits, but asks for a plaintext-containing file before destination readiness is established. Source inspection shows mapping and preview use the shared dialog without a Back callback; correcting a preview requires exiting and reselecting/reconfiguring. Changing any column mapping also rebuilds destination overrides.

**Change:** Explain destination prerequisites before file selection; offer a harmless sample format. Keep a compact step header and explicit Back from Preview to Mapping. Preserve non-secret mapping/destination choices where compatible; invalidate only affected mappings when their inputs change. After focus loss, keep clear reselect-file guidance and preserve only safe configuration choices, never source secret values.

**Acceptance:** An administrator with no writable destination receives a useful next step before reading the file. A mapping correction does not require starting the whole task again. No plaintext is persisted, and blur/cancellation and uncertain-write behavior retain the current protections.

**Evidence:** live first step; `import.js:162–181,189–240,243–284,285–300`. Later-step recovery was inspected in source, not exercised live.

### J6 · P2 · Make audit answer an investigation question before showing implementation details

**Observed:** The filter area simultaneously exposes actor ID, operation code, recorded actors and recorded operations. Common events still show `Group.Create`, `Resource.Create`, `Session.Identified`, `Requested`, `Committed` and `Success`. One logical mutation appears as separate Requested and Committed rows with no visible correlation explanation. An administrator asking “who changed this?” must interpret implementation phases.

**Change:** Start with time range, person, readable action and resource; put raw IDs/codes in Advanced filters. Translate ordinary action/outcome labels while retaining exact codes in details. Explain or group correlated mutation phases without hiding failed, incomplete or invalid-signature events; avoid pretending two records are two completed changes. Place snapshot capture/expiry context near refresh/export controls rather than only below the results.

**Acceptance:** A reviewer can distinguish requested versus completed changes and find the responsible actor without knowing codes. Raw signed evidence and both phases remain inspectable/exportable; invalid signatures remain conspicuous. Filter and snapshot/export contracts remain unchanged.

**Evidence:** live audit and filter task; `app.js:834–905`, especially 843–849 and 887–892.

### J7 · P3 · Simplify editing and assignment forms around the selected task

**Observed:** “Ändra lösenord” defaults safely to Preserve, but shows disabled username/password/notes, disabled Generate and disabled Read saved values together with a long explanation. It visually resembles an unavailable form even when renaming is allowed. The assignment substep is titled only “Tilldela licensplatser” and loses the product/resource and available-seat summary shown in its parent.

**Change:** Separate “Uppgifter” from an explicitly opened “Ersätt hemliga värden” area; show replacement fields only when that task is chosen. Keep the safe Preserve default and explicit audited loading. Carry license product, resource path and available seats into the assignment substep. Remove the disabled recipient-type control when only users are supported.

**Acceptance:** Renaming requires only changing the title and saving; it never reveals or replaces secrets. Replacement consequences remain explicit and blur resets safely. A person completing an assignment can see the license and available quantity without backing out.

**Evidence:** live password metadata and assignment forms; `app.js:485–510,794–817`.

## Cohesive structure and next implementation order

Keep the ordinary workspace centered on **Resources**, **Groups** and **Licenses**. Resource detail should consistently show its path, capability summary, searchable metadata sections and primary Show/Copy/Add actions. Treat changing metadata and replacing secret content as distinct intents within the same editor.

Give authorized administrators **Administration** with clearly scoped **Global roles**, **Resource access guidance**, **Import**, **Directory**, **Certificates and security**, and **About**. Keep **Audit** available according to auditor rights; its UI should support investigations while retaining a technical evidence view. These are navigation/presentation boundaries, not new roles or broader permissions.

Implement in this order:

1. Scope-aware navigation/headings, everyday capability wording, consistent breadcrumbs and actionable empty states (J1/J3).
2. Resource content filtering and simplified metadata/replacement/assignment forms (J2/J7).
3. Grant presets plus an exact scope/rights summary and import Back/recovery (J4/J5).
4. Human-readable audit filtering/results with preserved technical evidence and snapshot semantics (J6).
5. Complete the missing live reader/editor and 390 px mobile journeys, keyboard-only passes, and manual assistive-technology evaluation before describing the whole experience as accepted.

**Concrete first slice:** rename Settings to Administration and the global-only section to Global roles; give each section its own heading/description; always render the resource path; replace the raw ordinary-user permission summary with an accurate capability sentence; and make resource empty-state guidance conditional on Modify. Add no new roles, grants or metadata endpoints in this slice. Validate with reader/editor/access-admin-without-content fixtures before proceeding to the larger search/import/audit work.

Use a realistic synthetic fixture for acceptance: duplicate resource/person names, a deep group tree, a resource with dozens of records, a reader, an editor without ManageAccess, an access administrator without content rights, temporary access, empty content and one recoverable read failure. Measure task completion and incorrect turns rather than treating absence of Axe errors as task usability.

No application implementation or production state was changed by this review. The parent's concurrent feedback-test and follow-up-document changes were left untouched. Browser identity-header commands were rejected before applying; no viewport override was applied. At resumption CUA exposed no browser, so the earlier ephemeral review tab could not be explicitly closed or revisited. The parent-owned preview and role proxies were not stopped.
