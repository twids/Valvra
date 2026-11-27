# Navigation, links and history

The main menu, group names, resource cards, breadcrumbs and back links use ordinary links. They support opening in a new tab, copying the link address and browser history. Mouse back/forward buttons use the same history as the browser's buttons.

| Address | View |
| --- | --- |
| `/resources` | Resource overview |
| `/resources/{resourceId}` | A resource |
| `/groups` | Resource group hierarchy |
| `/groups/{groupId}` | A group with resources and subgroups |
| `/groups/{groupId}/resources/{resourceId}` | A resource opened from the group view |
| `/licenses` | License overview |
| `/audit` | Audit log, for auditors |
| `/settings` | Settings, for authorized administrators |

`/` opens the resource overview. IDs remain stable when a resource or group is renamed. The longer resource address retains the group as a back link; the resource may be in a subgroup. Moving it outside this subtree can make that link unavailable. The standalone `/resources/{resourceId}` address follows the resource even after a move.

Direct links and reloads are handled by explicit server routes in both the production app and the synthetic demo. IIS needs no additional rule to rewrite UI addresses. Unknown API and static file addresses are not rewritten to the app's HTML page. The installation wizard retains its existing `/Setup` address.

## Filters and keyboard

On the resources page, **Search the vault** finds resource names, account titles and license products. Open an account result to see the matching entries in its resource, or use **Copy password** on a readable account. The optional **My accounts** overview lists account titles and resource paths in pages of 50. Listing accounts never reveals a password. Use **Filters and view** to narrow the resource overview by group or change its layout. On a resource you manage, **Permissions** on the card opens access management directly. For an inherited grant or owner, **Manage in** opens the source group when you can manage it. Back returns to the original permissions dialog. Grant rows show scheduled, active, or expired validity.

Resource search, group filters, subgroup selection and audit filters, including page position, are retained in page memory. Back/Forward restores the filters for the current history entry. Reloading or opening a new tab opens the correct view with default filters; filters are not shared with a colleague through the link.

When navigating within the app or through history, focus moves to the new view's main heading. The initial page load retains the normal tab order, with **Skip to main content** as the first link.

## Permissions and sensitive information

An address never grants permission. The production app requires Windows sign-in for direct links too. The client selects only from metadata released by the server to the current user, and the server checks permissions for every content request. Invalid, deleted and unauthorized targets show the same message without fetching their content. The public demo opens only its fixed samples.

URLs contain the view and IDs, never secret values, names, search text or edit drafts. `history.state` contains only a random key to filters held in page memory. Secrets and form drafts are not included in these filter copies.

Dialogs for viewing, editing, permissions and history are temporary actions within the current view. Navigation closes the dialog and clears its fields. Forward shows the view again without opening the dialog, restoring an edit draft or making a new reveal request. Late responses from the view that was left must not add content to the new view.

Reloading can cancel an ongoing server request. Cancellation before preparation of an integrity checkpoint rolls back the database change. Once preparation begins, the server completes the already verified commit phase with an internal 30-second time limit, even if the client disconnects. An audit record may then remain in the signed outbox until the next delivery. Storage failures, uncertain commits and tampered data still follow the existing rules for denying access and requiring verified recovery.
