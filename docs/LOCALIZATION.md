# Språk / Languages

Valvra har svenska och engelska gränssnitt i samma applikation. Välj **Språk / Language** i sidhuvudet. Valet gäller också installationsguiden och det separata demoläget.

Första besöket använder webbläsarens språkpreferenser om svenska eller engelska finns där. Annars används svenska. Ett uttryckligt val sparas i `Valvra.Language` i ett år, för denna webbplats och webbläsare. Språkbytet laddar inte om sidan: installationsfält behåller sina värden. Vid omladdning gäller vanliga regler för osparade formulär. Inga formulärvärden eller hemligheter sparas i språkvalet.

English: choose **Language → English** in the header. The choice is remembered for subsequent visits and also applies to the installation wizard and the synthetic demo. No separate installation is needed.

## Vad översätts?

Menyer, rubriker, dialoger, fältetiketter, hjälptexter, valideringsmeddelanden, serverns felmeddelanden och installationskontroller översätts. HTML-språket och hjälpmedlens etiketter följer valet. Datum som presenteras av appen formateras med `sv-SE` respektive `en-GB`, i webbläsarens lokala tidszon. Webbläsarens egna datumkontroller och inloggningsdialoger kan även bero på operativsystemets och webbläsarens språk.

Resursnamn, gruppnamn, kontouppgifter, licensuppgifter och användarnas egna texter översätts inte. Demots svenska exempeldata behåller också sina namn. Audithändelsernas operations-ID:n, utfall och exporter behåller sina ursprungliga maskinläsbara värden; auditens lagrade tidsstämplar påverkas inte av språkvalet. Kryptering, signaturer, behörigheter och CSRF-kontroller har samma beteende för båda språken.

Språkkakan innehåller endast `sv` eller `en`. Den har `SameSite=Lax`, `Path=/` och `Secure` vid HTTPS. JavaScript behöver kunna läsa den; den är inget autentiseringsbevis. API-anrop skickar `X-Valvra-Language` så att svarens språk stämmer med gränssnittet även innan en kaka sparats. Servern väljer språk i ordningen explicit header, språkkaka, `Accept-Language`, svenska. Okända språk och felaktiga språkheaders ger säker återgång till ett språk som stöds och tilldelar inga rättigheter.

## För utvecklare

Gemensamma språkfiler finns i `src/Valvra.Web/wwwroot/i18n/sv.json` och `en.json`. Svenska originaltexter fungerar som nycklar. Lägg till båda översättningarna samtidigt. Använd namngivna parametrar som `{version}` när hela meningar behöver varierande värden; behåll samma parametrar i båda filerna.

I klienten används `ValvraI18n.message(key, parameters)` för gränssnittstexter och `ValvraI18n.setText(element, text)` för säker textutmatning. Översätt inte rå användardata och använd inte `innerHTML`. Lägg aldrig lösenord, licensnycklar eller andra hemliga värden i översättningsparametrar eller DOM-attribut. Statisk HTML använder explicita `data-i18n`-markeringar, även för `aria-label` när det behövs. Översättningsmarkeringar tas bort när ett element får rå användardata.

Servern använder `UiText` vid webbgränsen. Domänmodeller, feltyper, audithändelser och kryptografiska payloads lokaliseras inte. ASP.NETs kulturval följer [Microsofts dokumentation för RequestLocalization](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/localization/select-language-culture?view=aspnetcore-10.0). Okända valideringsfel får ett generiskt engelskt meddelande, så att interna detaljer inte råkar lämnas ut genom en fallback.

Kör `npm run test:security` för katalog- och klientregressioner, `dotnet test tests/Valvra.Tests -c Release --filter FullyQualifiedName~LocalizationTests` för serverns språkval och åtkomstgränser samt `npm run test:accessibility` för båda språkens webbläsarflöden. Inför release behöver manuell skärmläsarprovning göras på båda språken enligt [tillgänglighetsguiden](ACCESSIBILITY.md).
