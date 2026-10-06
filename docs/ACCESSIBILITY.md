# Grundläggande tillgänglighet i Valvra

Valvra utgår från relevanta grundkrav på nivå A och AA i [WCAG 2.2](https://www.w3.org/WAI/WCAG22/quickref/). Detta är en dokumenterad bas för gränssnittet, **inte ett påstående om full WCAG-överensstämmelse eller certifiering**. Kraven gäller både valvet, licenshanteringen och installationsguiden. Den syntetiska demon använder samma JavaScript och CSS.

## Implementerad bas

| Område | Genomförande | Relevanta WCAG-kriterier |
| --- | --- | --- |
| Struktur och språk | Svenskt sidspråk, huvudinnehåll, namngiven navigation, rubriker, formuläretiketter, grupperade rättigheter och databasanslutningar, tabellrubriker och tabellnamn. | 1.3.1, 3.1.1, 4.1.2 |
| Tangentbord | Hopplänk, vanliga HTML-kontroller, synligt fokus, fokus på rubriken vid vybyte, Escape stänger dialoger och fokus återgår till öppnande knapp eller sidrubrik. Native `dialog.showModal()` håller tabbordningen inne i dialogen. | 2.1.1, 2.1.2, 2.4.1, 2.4.3, 2.4.7 |
| Dialoger | Namngivna dialoger med initialt fokus på rubriken och tydlig stängknapp. Skärmläsaren kan läsa strukturen och formulärfälten i egen takt. Dialogens hela innehåll används inte som automatisk beskrivning. | 2.4.6, 4.1.2 |
| Kontrast och visuell presentation | Mörkare sekundärtext, varningsfärg och fältkanter. Tydlig fokusmarkering. Fel, installationsresultat och aktivt steg har text/semantik utöver färg. Stöd för Windows kontrasthöjande läge och minskad rörelse. | 1.4.1, 1.4.3, 1.4.11 |
| Förstoring och små skärmar | Relativa textstorlekar, omflöde och radbrytning. Tabeller får rullas i sidled i namngivna, tangentbordsfokuserbara områden; sidan som helhet ska inte kräva sidledes rullning. | 1.4.4, 1.4.10 |
| Kontroller | Minst 36 × 36 CSS-pixlar för knappar, 44 × 44 för stängknapp och minst 24 × 24 för kryssrutor. Upprepade radåtgärder får namn som anger vilken post de gäller. | 2.4.6, 2.5.8 |
| Formulär och feedback | Obligatoriska grundfält markeras, installationsfält har kopplade hjälptexter. Native validering kompletteras med `aria-invalid` och textfel i en alertregion. Statusmeddelanden och sökresultatantal använder separata statusregioner. | 3.3.1, 3.3.2, 4.1.3 |
| Installation | Aktivt steg anges med `aria-current="step"`. Nästa kontrollerar aktuella grundfält; rubriken får fokus när steget ändras och efter slutförd installation. Testresultat anges med ”Godkänd” eller ”Åtgärda”. | 2.4.3, 3.3.1, 4.1.2 |

## Säkerhet och tidsgränser

Tillgänglighetsfunktionerna ändrar inte serverns autentisering, behörigheter, kryptering eller audit. Hemligheter läggs inte i automatiska statusregioner. Skärmläsaren kan däremot läsa ett hemligt fält när användaren själv navigerar till det; tänk på vilka som kan höra uppläsningen.

Visade lösenord och licensnycklar töms och dialogen stängs efter **30 sekunder** eller vid fokusförlust. Redigeringsformulär behåller vanliga uppgifter, men hemliga fält töms vid fokusförlust eller **fem minuters inaktivitet**. Meddelandet om tömning innehåller inga hemliga värden. Vid ersättning måste värdet fyllas i igen innan Spara blir tillgängligt. Metadatautkast lagras endast i flikens minne.

Den fasta visningstiden är en **känd tillgänglighetsbegränsning**, särskilt för den som behöver längre tid eller använder skärmläsare. En ny visning kräver en ny behörighetskontrollerad och auditerad begäran. Vi hävdar inte att WCAG 2.2.1 (justerbara tidsgränser) är uppfyllt eller att ett säkerhetsundantag automatiskt gäller. En framtida lösning för justerbar visning måste säkerhetsgranskas innan tidsgränser ändras.

## Automatiska kontroller

Installera Node.js och projektets .NET SDK och kör från repots rot:

```powershell
npm ci
npx playwright install chromium
npm run test:security
npm run test:accessibility
```

På Linux kan webbläsarens systemberoenden behöva installeras med `npx playwright install --with-deps chromium`. Beroenden är versionslåsta i `package-lock.json` och behövs endast vid utveckling/test.

Tillgänglighetstesterna startar en separat .NET-testvärd på `localhost:58902` med syntetiska uppgifter och testidentitet samt den skrivskyddade demon på `127.0.0.1:58903`. Båda portarna ska vara lediga. Ingen anslutning till AD, riktiga databaser eller produktionsinstallation används. Testvärden ingår aldrig i IIS-paketet. Installationsanrop ersätts bara i testets isolerade webbläsarsession; tester av serverns installationsbehörigheter finns i C#-sviten.

Playwright och axe-core kontrollerar relevanta WCAG A/AA-regler på översikter, resurser, dialoger och installationssteg. Därutöver verifieras tangentbordsfokus, dialogens tabbordning, Escape, formulärfel, 320 CSS-pixlars bredd, dubblerad textstorlek, kontrastläge och att tömning av hemliga fält behåller metadatautkast. Rapporter och felspår sparas under `artifacts/accessibility-report` och `artifacts/accessibility-results`. Kör samma svit vid .NET-, webbläsar- och gränssnittsuppgraderingar. GitHub Actions har ett separat tillgänglighetsjobb.

## Manuell kontroll inför release

Automatiska tester kan inte avgöra om hela arbetsflödet är begripligt eller om en verklig skärmläsare fungerar väl. Följande ska kontrolleras på Windows inför release:

1. Navigera med enbart Tab, Shift+Tab, Enter, Space och Escape. Kontrollera att fokus är synligt och inte döljs av rullning, särskilt i långa dialoger och installationssteg.
2. Läs vyer och formulär med NVDA eller annan stödd skärmläsare. Kontrollera rubriker, tabeller, etiketter, hjälp, obligatoriska fält, fel, status och fokus vid vybyte. Sådan manuell skärmläsarprovning har inte genomförts i den automatiska sviten.
3. Prova 200 % textförstoring, 400 % sidzoom, 320 CSS-pixlars bredd, långa resursnamn och ändrat textavstånd. De automatiska omflödestesterna täcker representativa vyer; dessa ytterligare scenarier behöver manuell provning.
4. Kontrollera Windows kontrasthöjande läge och mobil peknavigation. Prova även den verkliga Windows-inloggningen, IIS och webbläsarens egna dialoger i installationsmiljön.
5. Bedöm tidsgränserna tillsammans med användare som behöver längre tid. Dokumentera återstående hinder innan en organisation gör en formell tillgänglighetsbedömning.

Fel rapporteras som GitHub-issues med vy, webbläsare, hjälpmedel och steg för att återskapa problemet. Bifoga aldrig riktiga lösenord eller licensnycklar. Organisationens eventuella tillgänglighetsredogörelse och lagkrav behöver bedömas separat för den faktiska installationen.
