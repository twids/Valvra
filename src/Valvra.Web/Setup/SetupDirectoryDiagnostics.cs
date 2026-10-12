using System.DirectoryServices.Protocols;
using Valvra.Core;

namespace Valvra.Web.Setup;

public static class SetupDirectoryDiagnostics
{
    // Localized guidance accompanies the original error in the protected setup UI.
    public static string Describe(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException)
                return "Active Directory-kontrollen kräver att Valvra körs på Windows.";
            if (current is AccessDeniedException)
                return "Ditt Windows-inloggade personkonto hittades inte inom katalogens sökbas eller är inaktiverat. Kontrollera sökbasen och vilket personkonto du loggat in med.";
            var code = current switch
            {
                LdapException ldap => ldap.ErrorCode,
                DirectoryOperationException { Response: { } response } => (int)response.ResultCode,
                _ => -1
            };
            var message = code switch
            {
                81 or 91 => "LDAPS-anslutningen kunde inte upprättas. Kontrollera domänkontrollantens fullständiga DNS-namn, port 636, nätverksåtkomst och certifikatets namn och betrodda kedja från IIS-servern.",
                85 => "LDAPS-kontrollen tog för lång tid. Kontrollera nätverksåtkomst och domänkontrollantens svarstid från IIS-servern.",
                49 or 48 => "Active Directory avvisade tjänstens autentisering. Kontrollera IIS-applikationspoolens domänkonto och dess möjlighet att autentisera mot domänkontrollanten. Ditt webbläsarkonto används inte för LDAP-anslutningen.",
                50 => "Tjänstens identitet saknar katalogläsrätt för en användar- eller gruppsökning. Kontrollera IIS-applikationspoolens konto och katalogbehörigheterna inom sökbasen.",
                32 or 34 => "Active Directory avvisade katalogens sökbas. Ange en befintlig sökbas som inkluderar ditt personkonto och de grupper som ska läsas, till exempel DC=example,DC=se.",
                8 or 13 => "Active Directory kräver starkare anslutningsskydd. Kontrollera LDAPS, tjänstens autentisering och domänens krav på LDAP-signering och kanalbindning.",
                _ => null
            };
            if (message is not null) return message;
        }
        return "Active Directory-kontrollen misslyckades vid anslutning eller användar- och gruppsökning. Kontrollera LDAPS, IIS-applikationspoolens identitet och katalogens sökbas.";
    }
}
