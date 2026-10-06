# Separate synthetic demo

`demo/Valvra.Demo` is a standalone .NET web app with no reference to the production server, EF, SQL, LDAP or certificate protection. It shows the same client interface with fixed public sample values. Do not enter real passwords, licenses or credentials.

Demo visitors have only metadata and read access to synthetic records. The server has no write/setup/permission/LDAP/audit API. Only the release endpoints for the two fixed sample records accept POST; their JSON may only select version 1 or copying. Other POST, PUT and DELETE requests are rejected. There is no database or persistent storage. The demo illustrates the interface; it does not verify production security, encryption, Windows SSO or signed audit records.

## Locally

```powershell
dotnet run --project demo/Valvra.Demo --urls http://127.0.0.1:58800
```

Open `http://127.0.0.1:58800`. Run it separately from the production app and do not provide production settings, certificates, database connections or `App_Data` mounts. The application rejects known production configuration sections and `VALVRA_CONFIG_DIR`.

## Docker / Dockhand

Publish only the demo project:

```powershell
dotnet publish demo/Valvra.Demo -c Release --self-contained false -p:UseAppHost=false -o artifacts/demo-publish
Copy-Item deploy/Demo.Dockerfile artifacts/demo-publish/Dockerfile
docker build -t local/valvra-demo:security-review artifacts/demo-publish
```

Use `deploy/demo.compose.yml` in Dockhand. It runs as the unprivileged `app` user, with a read-only filesystem, no capabilities and no data volumes. Only localhost port 15880 is published. To expose the demo through the organization's reverse proxy, connect only the demo container to the intended proxy network and create an HTTPS route to `valvra-demo:8080`. The production app and its test host must not be used as a public demo.

HTTP and client tests run through `dotnet test` and `node --test tests/browser/*.test.cjs`. The public demo identity is not a sign-in provider and is not included in the IIS package.
