# Contributing to Valvra

Contributions are welcome. Keep changes focused and describe the behavior, security implications and validation. Open an issue before proposing a new identity/database provider or a significant architecture change.

## License and contribution rights

Valvra is licensed under **EUPL-1.2 only**. Contributions are submitted under the same license. You retain copyright to your contribution and must have the right to submit it. Use `git commit -s` to certify the [Developer Certificate of Origin](https://developercertificate.org/). No copyright assignment or CLA is required.

## Required checks

- Build with .NET 10 and run `dotnet test`.
- Changes to persistence must pass tests on both MSSQL and PostgreSQL and include matching provider migrations.
- Changes to access, cryptography or audit require meaningful negative tests.
- Never commit real credentials, private keys, protected installation files or organization data.
- Do not suppress dependency vulnerability warnings to make CI pass.
- Keep the INSERT-only audit writer free of SELECT, UPDATE, DELETE and schema administration.

Use synthetic data in screenshots and tests. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
