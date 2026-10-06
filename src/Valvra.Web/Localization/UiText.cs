using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;

namespace Valvra.Web.Localization;

// The UI and server share the same catalogs. Request culture affects presentation
// only; audit action IDs, JSON, permissions and cryptographic payloads are unchanged.
public sealed class UiText
{
    private readonly Dictionary<string, string> english;
    public UiText(IWebHostEnvironment environment)
    {
        english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(environment.WebRootPath, "i18n", "en.json")))!;
    }

    public string Get(string source) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en"
        ? english.GetValueOrDefault(source, source) : source;

    public string Validation(string source) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en"
        ? english.GetValueOrDefault(source, "The supplied values could not be validated.") : source;

    public static RequestLocalizationOptions Options() => new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture("sv-SE"),
        SupportedCultures = [new("sv-SE"), new("en-GB")],
        SupportedUICultures = [new("sv-SE"), new("en-GB")],
        RequestCultureProviders = [new CustomRequestCultureProvider(context =>
        {
            var requested = context.Request.Headers["X-Valvra-Language"].ToString();
            if (requested is not ("sv" or "en")) requested = context.Request.Cookies["Valvra.Language"];
            if (requested is not ("sv" or "en"))
            {
                try
                {
                    requested = context.Request.GetTypedHeaders().AcceptLanguage?.Where(x => (x.Quality ?? 1) > 0)
                        .OrderByDescending(x => x.Quality ?? 1)
                        .Select(x => x.Value.ToString().Split('-')[0].ToLowerInvariant()).FirstOrDefault(x => x is "sv" or "en");
                }
                catch (FormatException) { requested = null; }
            }
            return Task.FromResult<ProviderCultureResult?>(new(requested == "en" ? "en-GB" : "sv-SE"));
        })]
    };
}
