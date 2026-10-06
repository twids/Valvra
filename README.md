# Valvra

An open-source password manager for organizations, built with .NET. Passwords and software licenses belong to resources in a hierarchical tree. Access is granted to AD users and groups, with separate permissions to read, modify and administer. Temporary read access and signed audit records are included.

The interface and installation wizard are available in **Swedish and English**. Choose **Language** in the header; your choice is remembered between visits. See [language support and how to add translations](docs/LOCALIZATION.md).

**License: EUPL-1.2.** Free to use in the public sector, businesses and privately under the [license terms](LICENSE). The project is under development; do not use real secrets until installation, recovery and a security review have been verified in your environment.

## Installation – Windows and IIS

### 1. Prepare the server

- A domain-joined Windows Server with IIS, **Windows Authentication** and an up-to-date [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0).
- A DNS name and a trusted HTTPS certificate for the website.
- MSSQL or PostgreSQL with a trusted TLS certificate. Create two empty databases: **Valvra** and **ValvraAudit**.
- A Windows service account, preferably **dMSA** in a prepared Windows Server 2025/AD environment, or gMSA, with directory read access in AD. For MSSQL, the vault and both audit connections use the same account through **Integrated Security**, without an account password in Valvra. See [environment requirements and database roles](docs/DATABASES.md).
- An active individual user account for the installer. The account that completes setup using the server's one-time code receives access and system administration permissions; global permissions are subsequently granted only to individual user accounts.

Audit runs **in the same service, in a separate database**. Grant the service account `valvra_audit_runtime` in the audit database: only `SELECT` and `INSERT`, with no permission to modify, delete or administer audit records. A separate reader account is optional. Installation accounts are used separately for schema changes. See [the audit guide and SQL scripts](docs/AUDIT.md).

For Kerberos SSO with a custom IIS service identity, the AD administrator must register the website's HTTP SPN on the service account, for example `setspn -S HTTP/valvra.example.se EXAMPLE\svcValvra$`. Check that the SPN is not registered on another account. Client browser policies need to allow automatic Windows sign-in to the website's DNS name.

### 2. Publish and install

Download `Valvra-1.0.1-win-x64.zip` from [release 1.0.1](https://github.com/twids/Valvra/releases/tag/v1.0.1) and extract it on the server. The package contains the application, `Install-Iis.ps1`, SQL scripts and documentation. Check the file's SHA-256 against the release's `SHA256SUMS.txt`. A fresh installation requires an empty vault; conversion of earlier preview data is not included. Updating from 1.0.0 requires no schema change: follow [the operations guide](docs/OPERATIONS.md#update-from-100-to-101) and preserve the protected configuration, integrity checkpoints and certificates. See [the changelog](CHANGELOG.md).

To build from source instead, use tag `v1.0.1` and the .NET 10 SDK from the repository root:

```powershell
dotnet restore
dotnet test
dotnet publish src/Valvra.Web -c Release -r win-x64 --self-contained false -o artifacts/publish
```

Copy the publish directory and `deploy/Install-Iis.ps1` to the server. Run the following in **Windows PowerShell as administrator**. Replace the values with your own:

```powershell
.\Install-Iis.ps1 -PublishedPath C:\Temp\ValvraPublish `
  -HostName valvra.example.se -HttpsCertificateThumbprint "YOUR-HTTPS-THUMBPRINT" `
  -ServiceAccount 'EXAMPLE\svcValvra$'
```

The script installs a new IIS website, sets file permissions, creates three separate cryptographic certificates for encryption, audit and vault integrity, and displays **the setup code and certificate thumbprints**. Store certificate backups securely as described in [the operations guide](docs/OPERATIONS.md). The script is intended for a fresh installation and does not overwrite an existing website.

### 2.1. Require protection for Windows sign-in

**Required before setup and production use:** the current `Install-Iis.ps1` enables Windows Authentication but does not set Extended Protection. Configure this manually. HTTPS alone does not prevent an attacker from relaying Windows authentication.

For the documented installation with HTTPS directly to IIS:

1. Open **IIS Manager → Sites → Valvra → Authentication**; use your website's name if it differs.
2. Check **Anonymous Authentication: Disabled** and **Windows Authentication: Enabled**.
3. Open **Windows Authentication → Advanced Settings** and set **Extended Protection: Required**. Read back the setting after saving. `Accept`/`Allow` is insufficient for this requirement.
4. Verify with a test account that SSO works and that authentication without valid channel binding is rejected. Document the result before production use; a successful setup wizard does not verify this IIS setting.

Use Kerberos with the correct HTTP SPN as described in step 1, and verify the protocol actually used with the AD/IIS administrator. `Negotiate` can fall back to NTLM. Any permitted NTLM also requires verified relay protection. See [Windows Authentication Providers](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/authentication/windowsauthentication/providers/).

A reverse proxy or TLS termination in front of IIS requires separate validation of channel/service binding across the entire connection. Follow [Microsoft's Extended Protection instructions](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/authentication/windowsauthentication/extendedprotection/). Do not lower protection to `Allow` or `None` to make such an installation work.

### 3. Follow the installation wizard in your browser

Open `https://valvra.example.se/setup` from a domain-joined client with Windows SSO:

1. Enter the one-time code and the website's DNS name.
2. Enter the vault and audit databases. Choose **Windows / Integrated Security** for both when using MSSQL. The same service account is used for audit reads and inserts; leave **Use a separate audit reader** unchecked for dMSA/gMSA without additional account passwords.
3. Enter the LDAPS server, directory lookup base and the three certificate thumbprints. If needed, enter separate search bases for individual user accounts and security groups, and an approved LDAP test server.
4. For a new database, choose to install schemas using **temporary installation accounts**. These accounts are not stored. Then grant the runtime accounts the documented database roles and test again without schema installation.
5. Click **Test connections and permissions**. Once all checks pass, click **Save and finish setup**.

Restart the `Valvra` IIS application pool and open the website again. The one-time code has now been consumed. Configuration is stored with DPAPI encryption in `App_Data`, with NTFS permissions restricted to the service and server administrators.

This version requires an **empty vault for a fresh installation**. Conversion of earlier preview data is not included. Setup creates a signed integrity checkpoint in `App_Data`, outside the database. Never give database administration identities access to these files or the service's private keys. A missing checkpoint must not be replaced by approving the contents of an existing database. Read and rehearse [backup and recovery](docs/OPERATIONS.md) before production use; the checkpoint is bound to the machine.

### 4. Create your first vault

Create a resource group, create a resource and grant an AD group read or modify permission. Then add passwords or licenses. Access administrators do not automatically receive permission to read secrets; their read access must also be granted explicitly.

Open **Settings** for routine administration: change the directory server and search bases, test the connection or grant global permissions to individual user accounts. **Save settings** verifies the connection and your account before saving; directory changes apply to new requests without a restart or one-time code. Databases and certificates are still changed through the operator's setup workflow. See [settings, roles and identity modules](docs/SETTINGS.md).

Open a group name in **Resource groups** to view its resources and navigate to subgroups. Resources in subgroups are included by default; uncheck **Include subgroups** to show only the group's own resources. Search and filters are retained when you open a resource and return. Breadcrumbs show the path through the groups you can access. Group administration actions are available under **Group actions**.

All main views, resource groups and resources have their own addresses. Mouse and browser **Back/Forward** navigation works; copy the address or use the link's menu to share it with a colleague. The colleague's own permissions apply. Direct links and reloads also work in the demo. See [navigation, links and history](docs/NAVIGATION.md).

## Operations and security

- [Audit, reads and inserts without modification rights](docs/AUDIT.md)
- [Databases, Windows Auth and permissions](docs/DATABASES.md)
- [Backup, recovery, certificate rotation and reconfiguration](docs/OPERATIONS.md)
- [Security model and limitations](docs/SECURITY-MODEL.md)
- [Verified tests and installation checks](docs/VERIFICATION.md)
- [Accessibility baseline and WCAG scope](docs/ACCESSIBILITY.md)
- [Report vulnerabilities privately](SECURITY.md)

Secrets are encrypted with AES-256-GCM and separate data keys, protected by an RSA certificate outside the database. Encryption binds the value to the installation, resource, record and version. A separate signed checkpoint also protects the resource structure, permissions and audit outbox against direct database changes. Audit events are signed before being stored in the outbox. No secrets are released if integrity or audit cannot be verified.

When editing a license, the existing key is preserved by default. **Replace** requires a new key; **Clear** is an explicit choice. Encrypted version history allows previous keys to be restored by a user with both read and modify permissions. Loss of focus and five minutes of inactivity clear sensitive fields while retaining ordinary information in open edit forms. The draft exists only in page memory and disappears on reload, cancellation or closing the tab. Revealed secrets are closed after 30 seconds or on loss of focus. A value already copied cannot be revoked.

## Development and contributions

A separate read-only demo with fixed sample data is described in [the demo guide](docs/DEMO.md). It can run on Linux without AD or databases and is not included in the production app.

MSSQL and PostgreSQL have separate EF Core migrations. Other databases require a new provider and verification. Sign-in, directory access, key protection, credential testing and audit have separate interfaces. The first version's production provider is AD/Windows SSO; local accounts and OIDC are not implemented.

```powershell
dotnet tool restore
dotnet restore
dotnet test
```

HTTP tests use test identities in the test project. The production application contains no simulated sign-in. Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes.

Also run the client security tests with Node.js: `node --test tests/browser/*.test.cjs`. For SDK/framework upgrade regressions, see [the verification guide](docs/VERIFICATION.md).

The interface has an accessibility baseline based on relevant WCAG 2.2 A/AA criteria. Run `npm ci`, `npx playwright install chromium` and `npm run test:accessibility` for automated accessibility and keyboard tests. See [the accessibility guide](docs/ACCESSIBILITY.md) for scope, manual release checks and the limitation of time-limited secret display.
