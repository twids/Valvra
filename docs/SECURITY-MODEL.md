# Säkerhetsmodell

Valvra är ett serverstyrt valv. Servern får dekryptera efter behörighetskontroll. En angripare som kontrollerar servern eller tjänsteidentiteten med privatnyckelåtkomst kan läsa hemligheter; detta är inte en zero-knowledge-lösning.

## Kryptering

AES-256-GCM med en ny slumpmässig datanyckel för varje hemlighetsversion. Autentiserad tilläggsdata binder innehållet till post-ID, version, format och innehållstyp. Datanycklar skyddas med RSA-OAEP/SHA-256 genom ett RSA-certifikat på minst 3072 bitar utanför databasen. Titlar, resursnamn och organisationsstruktur är synlig metadata. Kontonamn, lösenord, licensnycklar och hemliga anteckningar krypteras.

Datanyckel- och plaintext-bytebuffertar nollställs efter kryptografiska operationer. .NET-strängar, JSON-bindning och webbläsarens minne kan inte garanterat raderas. Hemligheter ska inte finnas i loggar, URL:er, beständig webbläsarlagring eller telemetry. UI tömmer visade hemligheter efter 30 sekunder eller när fönstret tappar fokus. Urklipp kan inte säkert återkallas av webbappen.

## Behörigheter

Alla kontroller sker på servern. AD-kontot och gruppmedlemskap hämtas inför API-operationer; AD-avbrott ger nekad åtkomst. Tilldelningar summeras från resursen och dess gruppträd. Inga explicita deny-regler eller brutet arv finns. Läs-, ändrings-, LDAP-test- och administratörsrätt är separata. Resursägare och centrala behörighetsadministratörer är betrodda och kan tilldela sig själva läsrätt.

Tillfällig läsrätt har exakt start- och sluttid i UTC och kontrolleras vid utlämning. Redan utlämnade värden kan inte återkallas. Vid behov måste lösenordet bytas i målsystemet.

## Inloggning och drift

Windows SSO utan extra MFA är det beslutade inloggningsflödet. Det bygger på domänens och klienternas skydd. Databaserna använder tjänstens identitet; ingen användardelegering till SQL görs. Servern kräver HTTPS och validerade databas-/LDAPS-certifikat. Aktivera IIS Windows Authentication och inaktivera Anonymous Authentication.

Installationen kräver Windows SSO och en slumpmässig 256-bitars engångskod som en serveroperatör genererar. Konfiguration skyddas med DPAPI LocalMachine **och NTFS-ACL**: DPAPI ersätter inte filbehörigheter. Ingen lokal lösenordsinloggning, simulerad användare eller hemlig webb-bakdörr finns.

## Audit

Audit kör i samma tjänst men separat databas, med INSERT-only skrividentitet och SELECT-only läsidentitet. Händelser signeras. Begränsningar och skydd mot manipulation/radering dokumenteras i [AUDIT.md](AUDIT.md). Individuella signaturer bevisar inte att historiken är fullständig.
