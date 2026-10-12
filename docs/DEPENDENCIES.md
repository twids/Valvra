# Dependency maintenance

Valvra targets .NET 10. Keep framework packages and the local `dotnet-ef` tool on
the current stable .NET 10 servicing release. A move to .NET 11 waits for its final
release; release candidates are not part of this dependency baseline.

NuGet versions are maintained in `Directory.Packages.props`. Central package
management and transitive pinning apply reviewed versions consistently across the
application, migrations and tests. `package-lock.json` locks npm dependencies;
GitHub Actions are pinned to release commit hashes.

## Version 1.2.1 baseline

Checked against NuGet, npm and official GitHub Action releases on 2026-10-12:

- Direct and transitive NuGet dependencies use the latest stable versions,
  except `Microsoft.Build.Framework` and its `Microsoft.NET.StringTools`
  dependency. Version 18.9.6 is the latest with explicit .NET 10 support;
  18.10.1 requires .NET 11. Do not suppress the framework compatibility warning
  to force an unsupported version into this release.
- Npgsql and its EF Core provider: 10.0.3. SQL Server client: 7.1.1.
- Serilog: 4.4.0, with File sink 7.0.0.
- xUnit v2: 2.9.3; Visual Studio adapter: 4.0.1; analyzers: 2.2.0.
- Playwright: 1.64.0; axe Playwright integration: 4.13.0.
- GitHub checkout: 7.0.1; setup-dotnet: 6.0.0; upload-artifact: 7.0.2.

SqlClient 7 separates Azure/Entra authentication into an optional extension.
Valvra's SQL password and Windows integrated authentication do not require it,
so unused Azure/MSAL dependencies are removed from the resolved package graph.
See [Microsoft's authentication documentation](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication).

NuGet, npm and GitHub Actions are checked weekly by Dependabot. Dependency
changes still require the Windows, accessibility and database CI checks before
squash merge. Updating packages does not establish production IIS/AD/SSO or
backup/restore compatibility; verify those in the target environment.

## Review commands

```powershell
dotnet list Valvra.slnx package --outdated --include-transitive
dotnet list Valvra.slnx package --vulnerable --include-transitive
npm outdated
npm audit
dotnet test Valvra.slnx -c Release
npm run test:security
npm run test:accessibility
npm run test:import
```

Review new transitive dependencies after each update: a newer direct dependency
can introduce a different set of packages. Keep the .NET 10 compatibility
exception visible rather than interpreting every outdated result as a failure.
