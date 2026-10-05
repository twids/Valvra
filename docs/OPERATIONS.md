# Drift, backup och återställning

## Vad som måste säkerhetskopieras

1. Valvdatabasen, inklusive krypterade datanycklar och audit-outbox.
2. Auditdatabasen och dess backuphistorik under separat behörighetskontroll.
3. Alla krypteringscertifikats privata nycklar i lösenordsskyddade PFX-backuper.
4. Auditcertifikatets privata nyckel och äldre publika certifikat för signaturverifiering.
5. Installationsinställningar och dokumenterade tjänsteidentiteter/rättigheter i organisationens skyddade dokumentation.

PFX-backuper och deras lösenord ska lagras separat från databasbackup. **Utan krypteringsnyckeln går lagrade lösenord inte att återställa.** `installation.bin` skyddas med maskinbunden Windows DPAPI och ska inte betraktas som en portabel konfigurationsbackup.

## Återställning på ny server

Återställ databaserna, importera krypteringscertifikat och auditcertifikat i LocalMachine/My och sätt privatnyckelbehörigheter för den nya tjänsteidentiteten. Installera webbappen och skapa en ny installationskod. Följ installationsguiden med de befintliga databaserna; **välj inte schemainstallation om schemat redan är aktuellt**. Ange tidigare krypteringsnycklar i skyddad konfiguration när rotation har genomförts. Verifiera en syntetisk hemlighet, äldre version, audit och databasrättigheter innan drift.

Genomför detta prov på en annan server före första produktionssättning. En backup som inte återställts är inte verifierad.

## Ändra installationen

På servern, som behörig operatör:

```powershell
C:\inetpub\Valvra\Valvra.Web.exe --initialize-setup
```

Öppna `/setup`, ange den nya engångskoden och fyll i inställningarna. Guiden returnerar inte tidigare lösenord eller privat konfiguration till webbläsaren. Efter sparande startas applikationspoolen om. Omkonfiguration ersätter den sparade konfigurationen; behåll därför befintliga inställningar och tidigare nyckelidentifierare i skyddad operatörsdokumentation.

## Certifikatrotation

Behåll äldre krypteringscertifikat tills alla datanycklar har ompaketerats och backupens återställningsperiod passerats. Äldre publika auditcertifikat behövs så länge äldre audit ska verifieras. Rotation av TLS-certifikatet är separat från dessa certifikat.

Ändra inte aktivt krypteringscertifikat utan att också behålla gamla tumavtryck i `KeyProtection:AllowedThumbprints`. Äldre auditcertifikat anges i `AuditDatabase:VerificationCertificateThumbprints`. Dessa listor måste följa med vid omkonfiguration. Nyckeladministration ska auditeras.

Efter att nya och gamla certifikat konfigurerats och valvets återställning verifierats: stoppa webbplatsens applikationspool under ett underhållsfönster och kör `Valvra.Web.exe --rewrap-keys` från publiceringsmappen. Operatören måste vara AD-behörighetsadministratör och ha åtkomst till certifikatens privatnycklar samt databaserna. Kommandot ompaketerar datanycklar och auditerar varje post; det behöver inte dekryptera lösenordsvärden. Vid avbrott kan det köras igen: poster som redan använder aktiv nyckel hoppas över. Behåll ändå tidigare nyckelbackup för äldre databasbackuper. Starta sedan applikationspoolen och verifiera nya och gamla hemlighetsversioner.

## Avbrott och övervakning

Auditdatabasens avbrott blockerar hemlighetsutlämning och nya skyddade ändringar. Ett fel efter lokal commit kan betyda att ändringen redan har genomförts. Kontrollera aktuell post före nytt manuellt försök. Outboxen återlevereras före nästa skyddade operation.

`/api/health` kräver Windows SSO och behörighetsadministratörsrätt. Endpointen kontrollerar audit genom en installationsliknande händelse och visar databasanslutning samt antal kvarvarande outboxposter. Larma på HTTP-fel, växande outbox, certifikatsproblem och misslyckade backups. Ett HTTP 200 ensamt bevisar inte full återställning.

Kör en IIS-arbetsprocess för applikationspoolen. LDAP-testets frekvensgräns är lokal för denna process och återställs vid omstart. Gör inte automatiska LDAP-test mot konton i produktion.
