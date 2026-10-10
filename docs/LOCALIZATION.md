# Languages

Valvra provides Swedish and English interfaces in the same application. Choose **Language** in the header. The choice also applies to the installation wizard and the separate demo mode.

On the first visit, the browser's language preferences are used if they include Swedish or English. Otherwise, Swedish is used. An explicit choice is stored in `Valvra.Language` for one year, for this website and browser. Changing language does not reload the page: installation fields retain their values. On reload, the usual rules for unsaved forms apply. No form values or secrets are stored in the language preference.

Choose **Language → English** in the header. The choice is remembered for subsequent visits and also applies to the installation wizard and the synthetic demo. No separate installation is needed.

## What is translated?

Menus, headings, dialogs, field labels, help text, validation messages, server error messages and installation checks are translated. The HTML language and assistive technology labels follow the choice. Dates displayed by the app are formatted using `sv-SE` or `en-GB`, in the browser's local time zone. The browser's own date controls and sign-in dialogs may also depend on the operating system and browser language.

Resource names, group names, account information, license information and users' own text are not translated. The demo's Swedish sample data also retains its names. Audit event operation IDs, outcomes and exports retain their original machine-readable values; stored audit timestamps are unaffected by the language choice. Encryption, signatures, permissions and CSRF checks behave identically in both languages.

The language cookie contains only `sv` or `en`. It uses `SameSite=Lax`, `Path=/` and `Secure` over HTTPS. JavaScript needs to read it; it is not proof of authentication. API requests send `X-Valvra-Language` so that the response language matches the interface even before a cookie has been stored. The server chooses a language in this order: explicit header, language cookie, `Accept-Language`, Swedish. Unknown languages and invalid language headers safely fall back to a supported language and grant no permissions.

## For developers

Shared language files are in `src/Valvra.Web/wwwroot/i18n/sv.json` and `en.json`. The original Swedish text serves as keys. Add both translations together. Use named parameters such as `{version}` when complete sentences need variable values; keep the same parameters in both files.

The client uses `ValvraI18n.message(key, parameters)` for interface text and `ValvraI18n.setText(element, text)` for safe text output. Do not translate raw user data or use `innerHTML`. Never put passwords, license keys or other secret values in translation parameters or DOM attributes. Static HTML uses explicit `data-i18n` markers, including for `aria-label` where needed. Translation markers are removed when an element receives raw user data.

The server uses `UiText` at the web boundary. Domain models, error types, audit events and cryptographic payloads are not localized. ASP.NET culture selection follows [Microsoft's RequestLocalization documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/localization/select-language-culture?view=aspnetcore-10.0). Unknown validation errors receive a generic English message to prevent internal details from being exposed through a fallback.

Run `npm run test:security` for catalog and client regressions, `dotnet test tests/Valvra.Tests -c Release --filter FullyQualifiedName~LocalizationTests` for server language selection and access boundaries, and `npm run test:accessibility` for browser workflows in both languages. Before release, manual screen reader testing is required in both languages as described in [the accessibility guide](ACCESSIBILITY.md).
