# Verifiering av första implementationen

## Automatiskt verifierat

GitHub Actions kör 50 tester, inklusive två tester mot verklig SQL Server 2022 och PostgreSQL 17 i isolerade, tillfälliga databaser. Windows-jobbet kör övriga 48 tester och publicerar IIS-paketet. Vid lokal körning utan testdatabaser hoppas endast de två databastesterna över.

Testerna omfattar bland annat:

- AES-GCM, manipulering av ciphertext, bindning till post/version och byte av nyckelskydd.
- Ärvda rättigheter, administratör utan automatisk läsrätt och exakt utgång av tillfällig åtkomst.
- Auditavbrott före utlämning/ändring, återleverans efter commit och förlorad commit-kvittens.
- Båda databasernas migrationer och faktiska INSERT-only/SELECT-only-rättigheter för audit.
- HTTP-avvisning, autentisering, CSRF, säkerhetsheaders och försök att läsa en annan användares post via ID.
- Installationskod, DPAPI-skydd och anslutningssträngar för tjänstens Windows-identitet.
- Separat rätt för LDAP-test, ett testanrop, tidsbegränsning och behörighetskontroll efter långsam audit.
- Versionshistorik, konfliktskydd, soft delete och licensplatser utan dekryptering av licensnyckel.

Resursvyn, lösenordsvisning, historik, behörighetsformulär, licensöversikt, audit och installationsguiden har även kontrollerats i webbläsare med syntetiska data. Testserver och testidentiteter ingår endast i testprojekten och publiceras inte i IIS-paketet.

CI använder lösenordsautentisering och undantag från TLS-verifiering **enbart i dess tillfälliga databaser**. Produktionskonfigurationen kräver betrodda certifikat. CI bevisar därför inte en viss organisations Kerberos-, LDAPS- eller certifikatkonfiguration.

## Kontroller vid installation i den egna miljön

En verklig domänansluten IIS/AD-installation har inte varit tillgänglig i utvecklingsmiljön. Följ README och låt setup-guiden verifiera tjänsteidentitetens databasanslutningar, katalog, certifikat och separata auditroller. Kontrollera därefter med testkonton och syntetiska hemligheter:

1. Windows SSO från klient, aktuell AD-grupptillhörighet och avvisning av inaktiverat konto.
2. En läsare, en användare med ändringsrätt och en användare utan tilldelning; kontrollera både gränssnitt och direkta API-anrop.
3. Tillfällig åtkomst före och efter utgångstid, samt signerad audit för läsning och ändring.
4. En godkänd LDAP-testprofil med ett särskilt testkonto och organisationens kontolåsningspolicy.
5. Backup och faktisk återställning av båda databaserna och kryptografiska nycklar på en isolerad server.

Denna kontroll behövs för varje installationsmiljö; automatiska tester ersätter inte en oberoende säkerhetsgranskning inför hantering av verkliga organisationshemligheter.
