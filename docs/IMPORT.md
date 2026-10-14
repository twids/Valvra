# Password import

Import requires the **Access Administrator** role and the destination's metadata and modify permissions. Creating hierarchy also requires management rights. Administrators do not automatically receive content access. The import endpoint enforces these requirements; normal content editors retain manual creation rights. Choose **Import passwords** from administration or a writable resource. Import supports UTF-8 CSV/TSV with a header row, at most 2 MiB, 64 columns and 1,000 entries. Comma, semicolon and tab separators are detected automatically or can be selected. Quoted delimiters, escaped quotes and multiline fields are supported. Other file encodings must be converted before import.

## Map and preview

The wizard suggests mappings for title, username, password, notes, URL, group path, subgroup and resource. Title and password columns are required; mapped columns must be distinct. Values are validated before any write. Passwords and notes retain their whitespace. URLs are appended to notes as text, rather than opened or fetched. Unmapped columns are ignored; licenses, attachments, TOTP settings and imported access rules are not supported.

Example:

```csv
title,username,password,group,resource
Database service,svc-example,example-only,IT/Servers,SQL
Switch administrator,admin,example-only,IT/Network,Core switch
```

Choose a base group. `IT/Servers` becomes two nested groups beneath it; `SQL` is the resource beneath `Servers`. Alternatively, map separate group and subgroup columns. Paths can use `/` or `\`; at most two nonempty levels are accepted. Group names containing those separators cannot be represented as a single path component. Blank paths use the base group directly. A missing resource name uses the editable fallback, initially `Import`.

Each distinct source path can instead be mapped to a visible existing group, bypassing creation of its source hierarchy. Another mode imports every entry into one writable existing resource regardless of the file's group/resource values.

Visible sibling groups and resources are reused by trimmed, Unicode-normalized, case-insensitive name. Ambiguous sibling names stop planning; choose a specific destination or resolve the ambiguity. Matching does not discover hidden objects. The preview shows destination paths and the number of new groups/resources. At most the first 100 entries are displayed, but all validated entries are included. Secret values are never shown in the preview.

## Permissions and storage

The wizard checks for a writable destination before asking for a file and includes a harmless CSV example. **Back** from Preview returns to Mapping with compatible column and destination choices retained; unrelated or temporarily invalid mapping edits do not discard compatible source-path overrides. Closing or losing focus still clears the staged file. **Select the file again** starts a fresh session with safe destination IDs/configuration retained; source secrets, column mappings and source-path overrides are discarded after focus loss. Nothing is imported merely by going Back or reselecting a file.

Existing destinations require metadata and modify permissions. Creation also requires permission to administer the destination group. New groups and resources inherit existing grants; import does not create grants or owners, and an administrator receives no automatic secret read/modify access. Establish the base group's intended access before import. Choose an existing writable resource if no new hierarchy is needed.

The file is parsed in browser memory. Source rows are never uploaded wholesale, stored in local/session storage, or placed in URLs. Selected operations are sent in bounded batches through the authenticated, anti-CSRF-protected `/api/imports/batch` endpoint; passwords are encrypted and each mutation is audited through the normal integrity-protected service. No existing secret is revealed for matching. Closing the wizard, losing focus or five minutes of inactivity discards staged source values and stops further requests after any in-flight operation. Browser/managed-runtime memory clearing is best effort, not guaranteed secure erasure. The source file on disk is unaffected; plaintext exports need secure handling by the operator.

## Duplicates and interruptions

At execution time existing title metadata is read for each destination. Titles already present in that resource, or repeated within the import, are skipped using trimmed, normalized, case-insensitive comparison. Import never updates, restores or overwrites an existing password. Matching by title is deliberately conservative and does not establish that two credentials are equivalent. Distinct accounts sharing a title must be renamed if both should be imported.

Each operation is separately committed and audited. The import is **not an all-or-nothing transaction**. The result reports saved/skipped entries and created groups/resources. If a request fails, no further operations are attempted. Already saved objects remain, including empty groups/resources. A server failure may identify a failed or uncertain source record; transport loss makes the entire last batch uncertain and may provide no record number. A lost response does not prove the server rejected its operations. Check the destination and audit before retrying. Record numbers refer to parsed records, not physical lines when quoted fields contain line breaks.

Preview is advisory: concurrent changes may happen before import. The server processes bounded batches of at most 12 operations, rechecking destination rights and duplicate names/titles under the protected operation lease. Each mutation is separately audited. The client also bounds serialized request size and only retries HTTP 429 rate-limit rejections, which occur before processing. It never retries uncertain writes or other failures automatically. Cancellation/focus loss stops subsequent batches; the current batch may finish. A lost response makes the whole last batch uncertain. Check the destination before retrying; matching existing titles are skipped rather than overwritten. Source values and raw exception text are excluded from failure reports.

This adds password import only. Secret export remains a separate design decision; the existing audit export contains audit metadata, not vault secret values.

## Verification

Run `node --test tests/browser/*.test.cjs` for parser, planning, duplicate, interruption and localization checks. Run `npm run test:import` after installing Node dependencies and Playwright Chromium. It builds an isolated synthetic host into `artifacts/import-preview`, uses an OS-assigned loopback port and verifies hierarchy reuse, duplicate preservation, signed audit, mapping, initial file-picker blur, overlapping-session cleanup, focus-loss clearing, English UI, mobile layout and automated accessibility. Backend `ImportApiTests` cover administrator enforcement, destination rights, CSRF, bounded batches, safe partial failures and denial auditing.
