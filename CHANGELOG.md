# Versionshistorik

## 1.0.0 – 2026-10-06

Första ordinarie releasen av Valvra för Windows/IIS, .NET 10 och AD/Windows SSO.

- Lösenord och programlicenser med krypterad versionshistorik, resursgrupper, ärvda behörigheter och tidsbegränsad läsrätt.
- AES-256-GCM med bindning till installation, resurs, post och version samt separat signerad kontrollpunkt för valvintegritet.
- Signerad audit i separat databas, behörighetsbegränsade databaskonton och historiska resurs-/gruppfilter för lista och export.
- SQL Server och PostgreSQL med separata migrationer och installationskontroller. Gemensam Windows-tjänsteidentitet för SQL Server-valv och audit stöds.
- Installationsguide, kataloginställningar, globala personkontoroller och godkända LDAP-testprofiler.
- Svenska och engelska, tangentbordsstöd, tillgänglighetsregressioner, direktlänkar och webbläsarhistorik.
- Separat skrivskyddad demo med syntetiska data. Demo och testidentiteter ingår inte i IIS-paketet.

Paket: `Valvra-1.0.0-win-x64.zip` och `SHA256SUMS.txt`. .NET 10 Hosting Bundle, IIS Windows Authentication, betrodda certifikat och korrekt konfigurerade databaser/AD krävs.

Nyinstallation kräver tomt valv. Ingen konvertering av tidigare preview-data ingår. Windows SSO, Extended Protection, dMSA/gMSA och backup/återställning ska verifieras i målmiljön enligt README och `docs/VERIFICATION.md`. Manuell skärmläsarprovning och oberoende säkerhetsgranskning har inte genomförts; automatiska tester innebär inte full WCAG-överensstämmelse.
