# Settings and administration

**Settings** is available in the main menu for access and system administrators. Routine directory administration requires no setup code or restart. All administrative API requests check permissions on the server; the menu is not the security boundary. Writes require a CSRF token and signed audit records.

## Directory connection

The system administrator can change the server, lookup base, user search base and security group search base. **Advanced connection** contains the LDAPS port and timeout. A trusted TLS certificate is required; certificate validation cannot be disabled. The connection uses the service's Windows identity and stores no LDAP password.

| Field | Purpose |
| --- | --- |
| Lookup base (`BaseDn`) | Signed-in account and current group memberships, including nested groups and the primary security group. |
| User search base (`UserSearchBaseDn`) | Searching for and validating active individual user accounts when granting access. Example: `OU=Users,DC=example,DC=se`. |
| Security group search base (`GroupSearchBaseDn`) | Searching for and validating security groups for resource permissions. Example: `OU=Groups,DC=example,DC=se`. Distribution groups are excluded. |

An empty user/group base uses the lookup base. Both must be within it. Restricting the group search base limits which groups can be selected; it does not remove memberships or previous resource permissions. Revoke such grants separately.

**Test connection** verifies your active account with the same provider and stable ID and shows up to five accounts/groups for the chosen search text. **Save settings** repeats the test and saves only after it succeeds. Failed tests leave saved configuration untouched. Drafts remain in page memory, including during language changes or loss of focus; reloading discards the draft. If a version conflict occurs, load the saved settings and make the change again.

The API returns only directory fields and the version number. Database passwords, connection strings, certificate settings and keys are not sent to this page. Saving updates an allowed set of fields in the DPAPI-protected configuration and preserves the remaining settings. Do not run installation and routine directory changes simultaneously; run one worker process as described in the operations guide.

The configuration file and audit database do not share a transaction. A delivered, signed intent precedes the change. If an error occurs after the file is written, the change may already apply despite an error message or a `Failed` event. Read back the saved settings and compare the version number before retrying. The outbox redelivers remaining signed events; `Failed` is not proof that the file was restored. Configuration recovery follows [OPERATIONS.md](OPERATIONS.md).

## Global permissions for individual accounts

| Permission | Allows |
| --- | --- |
| Access administrator | Administer resource access, owners and global permissions for individual accounts. |
| System administrator | Read and modify directory settings, test the directory connection and read global grants. |
| Auditor | Read, filter and export the audit log. |

The installer's active individual account receives both administrator permissions when setup is completed with the private one-time code. Audit read access is not included automatically. The first visitor to a completed vault does not become an administrator. An interrupted installation attempt can be continued by the same person; another person cannot take over the bootstrap grant already created.

Global permissions are stored in the vault's integrity-protected grants table as the installation's `System` grants, with the provider and stable user ID. AD groups or provider-reported administrator flags grant no global permissions. The ordinary resource permission API cannot create these grants. This addition requires no new tables; conversion of earlier preview installations is not included.

Use **Assign user account**, search for an account, select only the necessary permissions and save. Use **Modify** to revoke permissions; uncheck all permissions to remove the grant. Version checks protect concurrent changes. The last administrator for each administrator permission cannot be removed through the page, and a remaining replacement must be verifiable as active. Grants for disabled or deleted accounts can still be revoked.

Global permissions do not automatically allow password or license key decryption. **Both administrator roles are nevertheless highly trusted:** the access administrator can grant themselves read permission, and the system administrator controls which directory verifies identities and memberships. They are therefore not protected sandboxes for untrusted operators. Audit read access also exposes sensitive organizational metadata.

Maintain at least two designated individual accounts for the administrator permissions you need to preserve. AD can externally disable even the last administrator; the application cannot prevent that. Restore the account's access in AD or restore the correct configuration with the server operator. Setup on an existing installation does not reassign global roles. Do not change permissions directly in SQL or create a new checkpoint for existing data.

## Identity modules

The supplied implementation uses **Windows SSO** for sign-in and **Active Directory over LDAPS** as its directory. The page shows the installed modules. No OIDC, local password or other LDAP modules are supplied yet, and the existing provider cannot be changed from the page.

`ProviderRegistry` registers trusted sign-in/directory factories and descriptions in code. `IIdentityProvider` retrieves a stable ID from an already authenticated principal; `IDirectoryProvider` resolves active accounts/memberships and searches for and validates individual accounts/groups. The modules must share an identity namespace. The Web project must also register the new provider's actual authentication scheme and any secure configuration fields; the current scheme is Negotiate. No class names, DLL paths or module uploads are accepted from the browser.

Changing providers in an existing vault requires planned mapping of all user/group IDs, owners, license assignments and global roles. Automatic remapping is not included. Verify Windows/IIS/AD in your actual environment as described in [VERIFICATION.md](VERIFICATION.md); synthetic tests do not prove your Kerberos or LDAPS configuration.
