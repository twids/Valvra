# Inställningar och administration

**Inställningar** finns i huvudmenyn för behörighets- och systemadministratörer. Vanlig katalogadministration kräver ingen installationskod eller omstart. Alla administrativa API-anrop kontrollerar rättigheter på servern; menyn är inte säkerhetsgränsen. Skrivningar kräver CSRF-token och signerad audit.

## Kataloganslutning

Systemadministratören kan ändra server, uppslagsbas, personkontonas sökbas och säkerhetsgruppernas sökbas. Under **Avancerad anslutning** finns LDAPS-port och timeout. Betrott TLS-certifikat krävs; certifikatkontrollen kan inte stängas av. Anslutningen använder tjänstens Windows-identitet och sparar inget LDAP-lösenord.

| Fält | Användning |
| --- | --- |
| Uppslagsbas (`BaseDn`) | Inloggat konto och aktuella gruppmedlemskap, inklusive nästlade grupper och primär säkerhetsgrupp. |
| Personkontonas sökbas (`UserSearchBaseDn`) | Sökning och validering av aktiva personkonton vid tilldelning. Exempel: `OU=Users,DC=example,DC=se`. |
| Säkerhetsgruppernas sökbas (`GroupSearchBaseDn`) | Sökning och validering av säkerhetsgrupper vid resursbehörigheter. Exempel: `OU=Groups,DC=example,DC=se`. Distributionsgrupper ingår inte. |

Tom person-/gruppbas använder uppslagsbasen. Båda måste ligga inom den. En begränsad gruppsökbas begränsar vilka grupper som kan väljas; den tar inte bort medlemskap eller tidigare resursrättigheter. Återkalla sådana tilldelningar separat.

**Testa anslutning** verifierar ditt aktiva konto med samma provider och stabila ID och visar upp till fem konton/grupper för vald söktext. **Spara inställningar** gör testet igen och sparar först när det lyckas. Misslyckade tester lämnar sparad konfiguration orörd. Utkast finns i sidans minne, även vid språkbyte eller fokusförlust; omladdning stänger utkastet. Vid versionskonflikt: läs in sparade inställningar och gör ändringen igen.

API:t returnerar endast katalogfält och versionsnummer. Databaslösenord, anslutningssträngar, certifikatinställningar och nycklar skickas inte till denna sida. Sparandet uppdaterar en tillåten uppsättning fält i den DPAPI-skyddade konfigurationen och bevarar övriga inställningar. Installation och vanlig katalogändring ska inte köras samtidigt; kör en arbetsprocess enligt driftguiden.

Konfigurationsfilen och auditdatabasen har ingen gemensam transaktion. En levererad, signerad intent föregår ändringen. Om fel uppstår efter filskrivningen kan ändringen redan gälla trots ett felmeddelande eller en `Failed`-händelse. Läs tillbaka sparade inställningar och jämför versionsnumret innan ett nytt försök. Outbox återlevererar kvarvarande signerade händelser; `Failed` är inte ett bevis på att filen återställdes. Återställningen av konfiguration följer [OPERATIONS.md](OPERATIONS.md).

## Personbundna globala rättigheter

| Rättighet | Tillåter |
| --- | --- |
| Behörighetsadministratör | Administrera resursåtkomst, ägare och globala personrättigheter. |
| Systemadministratör | Läsa och ändra kataloginställningar, testa kataloganslutning och läsa globala tilldelningar. |
| Auditläsare | Läsa, filtrera och exportera auditloggen. |

Installatörens aktiva personkonto får de två administratörsrättigheterna när installationen slutförs med den privata engångskoden. Auditläsning ingår inte automatiskt. Första besökaren till ett färdigt valv blir inte administratör. Ett avbrutet installationsförsök kan fortsättas av samma person; en annan person får inte ta över den redan skapade bootstrap-tilldelningen.

Globala rättigheter lagras i valvets integritetsskyddade tilldelningstabell som installationens `System`-tilldelningar, med provider och stabilt person-ID. AD-grupper eller provider-rapporterade administratörsflaggor ger inga globala rättigheter. Resursbehörigheternas vanliga API kan inte skapa dessa tilldelningar. Inga nya tabeller krävs för detta tillägg; konvertering av äldre preview-installationer ingår inte.

Använd **Tilldela personkonto**, sök ett konto, markera endast nödvändiga rättigheter och spara. Använd **Ändra** för att återkalla; avmarkera alla för borttagning. Versionskontroll skyddar samtidiga ändringar. Den sista administratören för respektive administratörsrätt kan inte tas bort genom sidan, och en kvarvarande ersättare måste kunna verifieras som aktiv. Inaktiverade eller borttagna kontons tilldelningar kan fortfarande återkallas.

Globala rättigheter ger ingen automatisk dekryptering av lösenord eller licensnycklar. **Båda administratörsrollerna är ändå högt betrodda:** behörighetsadministratören kan tilldela sig själv läsrätt, och systemadministratören styr vilken katalog som verifierar identiteter och medlemskap. De är därför inte skyddade sandlådor för obetrodda operatörer. Auditläsning visar även känslig organisatorisk metadata.

Ha minst två utsedda personkonton för de administratörsrättigheter ni behöver upprätthålla. AD kan externt inaktivera även den sista administratören; applikationen kan inte förhindra det. Återställ kontots åtkomst i AD eller återställ rätt konfiguration med serveroperatören. Setup på en befintlig installation återutdelar inte globala roller. Ändra inte rättigheterna direkt i SQL och skapa inte en ny kontrollpunkt för befintliga data.

## Identitetsmoduler

Den levererade implementationen använder **Windows SSO** för inloggning och **Active Directory över LDAPS** som katalog. Sidan visar de installerade modulerna. Inga OIDC-, lokala lösenords- eller andra LDAP-moduler levereras ännu, och befintlig provider kan inte bytas från sidan.

`ProviderRegistry` registrerar betrodda login-/katalogfabriker och beskrivningar i kod. `IIdentityProvider` hämtar stabilt ID från en redan autentiserad principal; `IDirectoryProvider` löser aktiva konton/medlemskap samt söker och validerar personkonton/grupper. Modulerna måste dela identitetsnamnrymd. Web-projektet måste dessutom registrera den nya providerns verkliga autentiseringsscheme och eventuella säkra konfigurationsfält; dagens scheme är Negotiate. Inga klassnamn, DLL-sökvägar eller moduluppladdningar accepteras från webbläsaren.

Ett providerbyte i befintligt valv kräver planerad koppling av alla person-/grupp-ID:n, ägare, licenstilldelningar och globala roller. Automatisk omkoppling ingår inte. Verifiera Windows/IIS/AD i er riktiga miljö enligt [VERIFICATION.md](VERIFICATION.md); syntetiska tester bevisar inte er Kerberos- eller LDAPS-konfiguration.
