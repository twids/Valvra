# Navigering, länkar och historik

Huvudmenyn, gruppnamn, resurskort, brödsmulor och tillbaka-länkar använder vanliga länkar. De stöder öppning i ny flik, kopiering av länkadress och webbläsarens historik. Musens bakåt-/framåtknappar använder samma historik som webbläsarens knappar.

| Adress | Vy |
| --- | --- |
| `/resources` | Resursöversikt |
| `/resources/{resourceId}` | En resurs |
| `/groups` | Resursgruppernas hierarki |
| `/groups/{groupId}` | En grupp med resurser och undergrupper |
| `/groups/{groupId}/resources/{resourceId}` | En resurs öppnad från gruppens vy |
| `/licenses` | Licensöversikt |
| `/audit` | Auditlogg, för auditläsare |
| `/settings` | Inställningar, för behöriga administratörer |

`/` öppnar resursöversikten. ID:n är stabila när en resurs eller grupp får ett nytt namn. Den längre resursadressen behåller gruppen som tillbaka-länk; resursen kan ligga i en undergrupp. En flytt ut ur detta underträd kan göra den länken otillgänglig. Den fristående `/resources/{resourceId}`-adressen följer resursen även efter en flytt.

Direktlänkar och omladdning hanteras av uttryckliga serverrutter i både produktionsappen och den syntetiska demon. IIS behöver ingen extra regel för att skriva om UI-adresser. Okända API- och statiska filadresser skrivs inte om till appens HTML-sida. Installationsguiden behåller sin befintliga `/Setup`-adress.

## Filter och tangentbord

Resurssökning, gruppfilter, val av undergrupper och auditfilter inklusive sidposition behålls i sidans minne. Bakåt/Framåt återställer filtren för det aktuella historiksteget. Omladdning eller en ny flik öppnar rätt vy med standardfilter; filter skickas inte till kollegan genom länken.

Vid navigering inom appen och vid historiknavigering flyttas fokus till den nya vyns huvudrubrik. Första sidladdningen behåller normal tabbordning, där **Hoppa till huvudinnehållet** är första länken.

## Behörighet och känsliga uppgifter

En adress ger aldrig behörighet. Produktivappen kräver Windows-inloggning också på direktlänkar. Klienten väljer endast bland metadata som servern har lämnat ut till den aktuella användaren, och servern kontrollerar behörigheten för varje innehållsanrop. Ogiltiga, borttagna och otillåtna mål ger samma meddelande utan att hämta målens innehåll. Den publika demon öppnar endast sina fasta exempel.

URL:er innehåller vy och ID:n, aldrig hemliga värden, namn, söktext eller redigeringsutkast. `history.state` innehåller endast en slumpmässig nyckel till filter som ligger i sidans minne. Hemligheter och formulärutkast ingår inte i dessa filterkopior.

Dialoger för visning, redigering, behörigheter och historik är tillfälliga åtgärder i aktuell vy. Navigering stänger dialogen och tömmer dess fält. Framåt visar vyn igen och öppnar inte dialogen, återställer inget redigeringsutkast och gör inget nytt reveal-anrop. Sena svar från den lämnade vyn får inte lägga till innehåll i den nya vyn.

En omladdning kan avbryta ett pågående serveranrop. Avbrott före förberedelsen av en integritetskontrollpunkt rullar tillbaka databasändringen. När förberedelsen börjar slutför servern den redan verifierade commit-fasen med en intern tidsgräns på 30 sekunder, även om klienten kopplar ned. En auditpost kan därefter finnas kvar i den signerade outboxen tills nästa leverans. Lagringsfel, osäkra commits och manipulerad data följer fortfarande de befintliga reglerna för att neka åtkomst och kräva verifierad återhämtning.
