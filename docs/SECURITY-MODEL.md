# Säkerhetsmodell

Valvra är ett serverstyrt valv. Servern får dekryptera efter behörighetskontroll. En angripare som kontrollerar servern eller tjänsteidentiteten med privatnyckelåtkomst kan läsa hemligheter; detta är inte en zero-knowledge-lösning.

## Kryptering

AES-256-GCM med en ny slumpmässig datanyckel för varje hemlighetsversion. Autentiserad tilläggsdata binder innehållet till installations-ID, resurs-ID, post-ID, version, format och innehållstyp. Format 2 accepterar inte tidigare obundna envelopes. Datanycklar skyddas med RSA-OAEP/SHA-256 genom ett RSA-certifikat på minst 3072 bitar utanför databasen. Titlar, resursnamn och organisationsstruktur är synlig metadata. Kontonamn, lösenord, licensnycklar och hemliga anteckningar krypteras.

Datanyckel- och plaintext-bytebuffertar nollställs efter kryptografiska operationer. .NET-strängar, JSON-bindning och webbläsarens minne kan inte garanterat raderas. Hemligheter ska inte finnas i loggar, URL:er, beständig webbläsarlagring eller telemetry. UI tömmer visade hemligheter efter 30 sekunder eller när fönstret tappar fokus. Urklipp kan inte säkert återkallas av webbappen.

Fokusförlust tömmer även känsliga redigeringsfält. Vanliga formuläruppgifter behålls. Sparande av licensmetadata kräver ingen dekryptering och bevarar befintlig nyckel. Ersättning kräver en icke-tom ny nyckel; tömning kräver explicit val. Varje nyckeländring skapar en krypterad historikversion. Återställning kräver både läs- och ändringsrätt. Sena utlämningssvar efter fokusförlust eller byte av dialog visas inte och kopieras inte till urklipp.

## Integritet mot en databasangripare

GCM skyddar ciphertext, men kan inte ensam skydda vilka användare som ges åtkomst till vilken resurs. Därför verifierar tjänsten hela valvets tillstånd innan behörighetsbeslut och operationer. Kontrollpunkten innehåller installations-ID, generation och SHA-256-hash av samtliga tio domäntabeller, inklusive ägare, rättigheter, versioner och audit-outbox. Den signeras med ett tredje, separat RSA-PSS-certifikat och lagras DPAPI-skyddad i `App_Data` med begränsad NTFS-ACL.

En databasidentitet får inte ha åtkomst till kontrollpunktsfiler eller tjänstens privatnycklar. Detta är en förutsättning: serveradministratörer och tjänsteidentiteten är betrodda. Skyddet upptäcker databasändringar, radering, tillägg och återställning av äldre valvdata; det förhindrar inte att en databasadministratör orsakar driftstopp.

En processoberoende filspärr och serialiserbar databastransaktion håller verifiering och operation tillsammans. Endast EF-spårade, avsedda ändringar får skapa nästa signerade kontrollpunkt; oväntade triggerändringar godkänns inte. Kontrollpunkten förbereds före commit och slutförs efteråt. Efter avbrott accepteras automatiskt endast exakt det förberedda nya tillståndet. Gammalt tillstånd kräver uttrycklig operatörsåterhämtning; ett okänt tillstånd nekas. Se [driftguiden](OPERATIONS.md).

Klientavbrott respekteras före förberedelsen. Från förberedelse till slutförd commit används en intern tidsgräns på 30 sekunder, oberoende av klientens anslutning, för att undvika en halvfärdig kontrollpunkt vid exempelvis snabb omladdning. Detta kan slutföra en redan verifierad ändring efter att klienten lämnat sidan. Fel i lagring eller commit försvagar inte integritetskontrollerna eller kraven för återhämtning.

Den första implementationen läser hela tillståndet vid varje verifiering och skyddat sparande, och serialiserar operationer. Datamängd och växande outbox påverkar därför svarstid och minne. Kör en arbetsprocess på en server; stöd för distribuerade instanser och skalning är inte verifierat. Belastningsprova representativa datamängder innan drift. Automatisk ombasering eller automatisk borttagning av kontrollpunkten är förbjuden.

## Behörigheter

Alla kontroller sker på servern. AD-kontot och gruppmedlemskap hämtas inför API-operationer; AD-avbrott ger nekad åtkomst. Tilldelningar summeras från resursen och dess gruppträd. Inga explicita deny-regler eller brutet arv finns. Läs-, ändrings-, LDAP-test- och administratörsrätt är separata. Resursägare och centrala behörighetsadministratörer är betrodda och kan tilldela sig själva läsrätt.

Tillfällig läsrätt har exakt start- och sluttid i UTC och kontrolleras vid utlämning. Redan utlämnade värden kan inte återkallas. Vid behov måste lösenordet bytas i målsystemet.

Globala rättigheter är personbundna, integritetsskyddade tilldelningar. AD-grupper och katalogproviderflaggor ger inga globala rättigheter. Installatören får behörighets- och systemadministration vid skyddad setup, utan automatisk auditläsning eller läsrätt till hemligheter. Systemadministration är högt betrodd eftersom rollen styr katalogens verifiering av identiteter. Se [SETTINGS.md](SETTINGS.md) för roller, sökbaser, modulgränser och konfigurationssparandets auditbegränsning.

Gruppträd får ha högst 128 nivåer. Både skapande och flytt kontrollerar hela underträdets djup före ändring, så att en delegerad administratör inte kan skapa ett träd som gör valvets listning oanvändbar.

## Inloggning och drift

Windows SSO utan extra MFA är det beslutade inloggningsflödet. Det bygger på domänens och klienternas skydd. Databaserna använder tjänstens identitet; ingen användardelegering till SQL görs. Servern kräver HTTPS och validerade databas-/LDAPS-certifikat. Aktivera IIS Windows Authentication och inaktivera Anonymous Authentication.

Följ även det obligatoriska steget [Kräv skydd för Windows-inloggningen](../README.md#21-kräv-skydd-för-windows-inloggningen). Installationsskriptet och setup-guiden verifierar ännu inte detta skydd automatiskt.

Installationen kräver Windows SSO och en slumpmässig 256-bitars engångskod som en serveroperatör genererar. Konfiguration skyddas med DPAPI LocalMachine **och NTFS-ACL**: DPAPI ersätter inte filbehörigheter. Ingen lokal lösenordsinloggning, simulerad användare eller hemlig webb-bakdörr finns.

## Audit

Audit kör i samma tjänst men separat databas. Standardinstallationen använder samma tjänstekonto med SELECT och INSERT, utan ändrings-, raderings- eller administrationsrätt i auditdatabasen; separata skriv-/läsidentiteter är valbara. Auditläsarrollen i applikationen krävs fortfarande för åtkomst till historiken. Händelser signeras. Begränsningar och skydd mot manipulation/radering dokumenteras i [AUDIT.md](AUDIT.md). Individuella signaturer bevisar inte att historiken är fullständig.
