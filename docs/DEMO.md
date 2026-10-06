# Separat syntetisk demo

`demo/Valvra.Demo` är en egen .NET-webbapp utan referens till produktionsservern, EF, SQL, LDAP eller certifikatskydd. Den visar samma klientgränssnitt med fasta offentliga exempelvärden. Inga riktiga lösenord, licenser eller inloggningsuppgifter ska matas in.

Demobesökaren har endast metadata- och läsrätt för syntetiska poster. Servern saknar skriv-/setup-/behörighets-/LDAP-/audit-API. Endast de två fasta exempelposternas utlämningsvägar accepterar POST; deras JSON får enbart välja version 1 eller kopiering. Andra POST, PUT och DELETE avvisas. Det finns ingen databas eller beständig lagring. Demon illustrerar gränssnittet; den verifierar inte produktionssäkerhet, kryptering, Windows SSO eller signerad audit.

## Lokalt

```powershell
dotnet run --project demo/Valvra.Demo --urls http://127.0.0.1:58800
```

Öppna `http://127.0.0.1:58800`. Starta den separat från produktionsappen och ge den inga produktionsinställningar, certifikat, databasanslutningar eller `App_Data`-mounts. Applikationen avvisar kända produktionskonfigurationssektioner och `VALVRA_CONFIG_DIR`.

## Docker / Dockhand

Publicera enbart demoprojektet:

```powershell
dotnet publish demo/Valvra.Demo -c Release --self-contained false -p:UseAppHost=false -o artifacts/demo-publish
Copy-Item deploy/Demo.Dockerfile artifacts/demo-publish/Dockerfile
docker build -t local/valvra-demo:security-review artifacts/demo-publish
```

Använd `deploy/demo.compose.yml` i Dockhand. Den kör som den obehöriga `app`-användaren, med skrivskyddat filsystem, utan capabilities och utan datavolymer. Endast localhost-port 15880 publiceras. Om demon ska nås via organisationens reverse proxy, anslut endast democontainern till det avsedda proxynätverket och skapa en HTTPS-route mot `valvra-demo:8080`. Produktionsappen och dess testhost får inte användas som publik demo.

HTTP- och klienttester körs via `dotnet test` och `node --test tests/browser/*.test.cjs`. Den publika demoidentiteten är inte en inloggningsprovider och finns inte i IIS-paketet.
