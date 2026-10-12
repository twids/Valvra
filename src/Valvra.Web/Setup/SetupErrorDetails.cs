using System.DirectoryServices.Protocols;
using System.Text;

namespace Valvra.Web.Setup;

public static class SetupErrorDetails
{
    public static string Format(Exception exception, IEnumerable<string?> sensitiveValues)
    {
        var text = new StringBuilder();
        void Append(Exception error)
        {
            if (text.Length > 0) text.AppendLine();
            text.Append(error.GetType().Name).Append(": ").Append(error.Message);
            if (error is LdapException ldap)
            {
                text.AppendLine().Append("LDAP error code: ").Append(ldap.ErrorCode);
                if (!string.IsNullOrWhiteSpace(ldap.ServerErrorMessage))
                    text.AppendLine().Append(ldap.ServerErrorMessage);
            }
            if (error is DirectoryOperationException { Response: { } response })
            {
                text.AppendLine().Append("LDAP result: ").Append(response.ResultCode);
                if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                    text.AppendLine().Append(response.ErrorMessage);
            }
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) Append(inner);
            else if (error.InnerException is { } inner) Append(inner);
        }
        Append(exception);
        var details = text.ToString();
        foreach (var secret in sensitiveValues.Where(value => !string.IsNullOrEmpty(value))
                     .Distinct(StringComparer.Ordinal).OrderByDescending(value => value!.Length))
            details = details.Replace(secret!, "[redacted]", StringComparison.Ordinal);
        return details;
    }
}
