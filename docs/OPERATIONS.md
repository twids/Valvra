# Drift, backup och återställning

## Vad som måste säkerhetskopieras

1. Valvdatabasen, inklusive krypterade datanycklar och audit-outbox.
2. Auditdatabasen och dess backuphistorik under separat behörighetskontroll.
3. Alla krypteringscertifikats privata nycklar i lösenordsskyddade PFX-backuper.
4. Auditcertifikatets privata nyckel och äldre publika certifikat för signaturverifiering.
5. Integritetscertifikatets privata nyckel och äldre publika certifikat, samt hela `App_Data` inklusive `installation.bin` och `Integrity/checkpoint.bin`.
6. Windows system-/maskinbackup som bevarar DPAPI-maskinnycklarna, och dokumenterade tjänsteidentiteter/rättigheter i organisationens skyddade dokumentation.

PFX-backuper och deras lösenord ska lagras separat från databasbackup. **Utan krypteringsnyckeln går lagrade lösenord inte att återställa.** Både `installation.bin` och kontrollpunkten skyddas med maskinbunden Windows DPAPI. Databas + PFX + kopierad `App_Data` räcker inte ensamma för återställning på en ny Windows-installation.

Stoppa IIS-applikationspoolen och eventuella operatörskommandon när valvdatabasen och kontrollpunkten säkerhetskopieras. Ta en samordnad kopia av dessa innan tjänsten startas igen. En databasbackup från en annan generation än kontrollpunkten nekas avsiktligt. Auditdatabasen säkerhetskopieras separat och befintlig senare audit ska bevaras vid valvåterställning. Dokumentera vilka kopior som hör ihop och skydda backupåtkomsten separat från databasens runtime-konton.

## Återställning av samma skyddade installation

Stoppa tjänsten. Återställ ett verifierat, samordnat par av valvdatabas och kontrollpunkt till den Windows-installation som kan dekryptera DPAPI-filerna. Återställ nödvändiga certifikat i LocalMachine/My och begränsa privatnyckelåtkomst till tjänsteidentiteten. Bevara installations-ID och tidigare nyckelidentifierare. Starta tjänsten och verifiera med syntetiska poster att rättigheter, gamla och nya versioner samt audit fungerar.

Återställning till annan maskin kräver en verifierad återställning av Windows-maskinens DPAPI-kontext, exempelvis via organisationens maskinbackup. **Portabel export/import av kontrollpunkt till en ny Windows-installation är inte implementerad.** Setup kan inte godkänna en befintlig databas utan en giltig kontrollpunkt. Skapa inte en ny tom kontrollpunkt över återställd data och radera inte checkpoint-filen för att komma förbi ett integritetsfel.

Genomför detta prov på en annan server före första produktionssättning. En backup som inte återställts är inte verifierad.

## Avbruten integritetsuppdatering

Kontrollpunkten kan innehålla ett godkänt tillstånd och ett förberett nästa tillstånd. Om databasen exakt motsvarar det förberedda tillståndet slutför tjänsten kontrollpunkten automatiskt. Om den motsvarar det äldre tillståndet blockeras åtkomst; tjänsten gissar inte om det var ett avbrott eller en rollback-attack.

Efter separat kontroll av avbrott, databas och audit får en AD-behörighetsadministratör, med applikationspoolen stoppad och rätt certifikat-/databasåtkomst, köra:

```powershell
C:\inetpub\Valvra\Valvra.Web.exe --recover-integrity --discard-uncommitted
```

Kommandot tar endast bort en förberedd kontrollpunkt om databasens exakta hash fortfarande matchar den signerade föregående kontrollpunkten. Det godkänner inte godtycklig data och återskapar inte saknad kontrollpunkt. Operatörsåterhämtning auditeras. Vid andra avvikelser ska orsaken utredas och en verifierad samordnad backup användas.

## Ändra installationen

På servern, som behörig operatör:

```powershell
C:\inetpub\Valvra\Valvra.Web.exe --initialize-setup
```

Öppna `/setup`, ange den nya engångskoden och fyll i inställningarna. Guiden returnerar inte tidigare lösenord eller privat konfiguration till webbläsaren. Efter sparande startas applikationspoolen om. Omkonfiguration ersätter den sparade konfigurationen; behåll därför befintliga inställningar och tidigare nyckelidentifierare i skyddad operatörsdokumentation.

## Certifikatrotation

Behåll äldre krypteringscertifikat tills alla datanycklar har ompaketerats och backupens återställningsperiod passerats. Äldre publika auditcertifikat behövs så länge äldre audit ska verifieras. Äldre integritetscertifikat behövs för aktuell och säkerhetskopierad kontrollpunkt. Rotation av TLS-certifikatet är separat från dessa tre certifikat.

Ändra inte aktivt krypteringscertifikat utan att också behålla gamla tumavtryck i `KeyProtection:AllowedThumbprints`. Äldre auditcertifikat anges i `AuditDatabase:VerificationCertificateThumbprints`. Dessa listor måste följa med vid omkonfiguration. Nyckeladministration ska auditeras.

För integritet anges tidigare tumavtryck i `Integrity:VerificationCertificateThumbprints`. Behåll samma installations-ID. Omkonfiguration verifierar den befintliga databasens kontrollpunkt; den initierar inte om valvet. Nästa skyddade skrivning signerar nästa generation med aktivt integritetscertifikat.

Efter att nya och gamla certifikat konfigurerats och valvets återställning verifierats: stoppa webbplatsens applikationspool under ett underhållsfönster och kör `Valvra.Web.exe --rewrap-keys` från publiceringsmappen. Operatören måste vara AD-behörighetsadministratör och ha åtkomst till certifikatens privatnycklar samt databaserna. Kommandot ompaketerar datanycklar och auditerar varje post; det behöver inte dekryptera lösenordsvärden. Vid avbrott kan det köras igen: poster som redan använder aktiv nyckel hoppas över. Behåll ändå tidigare nyckelbackup för äldre databasbackuper. Starta sedan applikationspoolen och verifiera nya och gamla hemlighetsversioner.

## Avbrott och övervakning

Auditdatabasens avbrott blockerar hemlighetsutlämning och nya skyddade ändringar. Ett fel efter lokal commit kan betyda att ändringen redan har genomförts. Kontrollera aktuell post före nytt manuellt försök. Outboxen återlevereras före nästa skyddade operation.

`/api/health` kräver Windows SSO och behörighetsadministratörsrätt. Endpointen kontrollerar audit genom en installationsliknande händelse och visar databasanslutning samt antal kvarvarande outboxposter. Larma på HTTP-fel, växande outbox, certifikatsproblem och misslyckade backups. Ett HTTP 200 ensamt bevisar inte full återställning.

Kör en IIS-arbetsprocess för applikationspoolen. LDAP-testets frekvensgräns är lokal för denna process och återställs vid omstart. Gör inte automatiska LDAP-test mot konton i produktion.
