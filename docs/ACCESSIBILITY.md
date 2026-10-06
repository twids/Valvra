# Accessibility baseline in Valvra

Valvra is based on relevant Level A and AA requirements in [WCAG 2.2](https://www.w3.org/WAI/WCAG22/quickref/). This is a documented interface baseline, **not a claim of full WCAG conformance or certification**. The requirements apply to the vault, license management and installation wizard. The synthetic demo uses the same JavaScript and CSS.

## Implemented baseline

| Area | Implementation | Relevant WCAG criteria |
| --- | --- | --- |
| Structure and language | Swedish page language, main content, named navigation, headings, form labels, grouped permissions and database connections, table headers and table names. | 1.3.1, 3.1.1, 4.1.2 |
| Keyboard | Skip link, standard HTML controls, visible focus, heading focus when changing views, Escape closes dialogs and focus returns to the opening button or page heading. Native `dialog.showModal()` keeps the tab order within the dialog. | 2.1.1, 2.1.2, 2.4.1, 2.4.3, 2.4.7 |
| Dialogs | Named dialogs with initial heading focus and a clear close button. Screen readers can read the structure and form fields at their own pace. The entire dialog content is not used as an automatic description. | 2.4.6, 4.1.2 |
| Contrast and visual presentation | Darker secondary text, warning color and field borders. Clear focus indicators. Errors, installation results and the active step have text/semantics in addition to color. Support for Windows high contrast mode and reduced motion. | 1.4.1, 1.4.3, 1.4.11 |
| Magnification and small screens | Relative text sizes, reflow and line wrapping. Tables may scroll horizontally in named, keyboard-focusable areas; the page as a whole should not require horizontal scrolling. | 1.4.4, 1.4.10 |
| Controls | At least 36 × 36 CSS pixels for buttons, 44 × 44 for the close button and at least 24 × 24 for checkboxes. Repeated row actions are named to identify the record they affect. | 2.4.6, 2.5.8 |
| Forms and feedback | Required basic fields are marked, and installation fields have associated help text. Native validation is supplemented with `aria-invalid` and text errors in an alert region. Status messages and search result counts use separate status regions. | 3.3.1, 3.3.2, 4.1.3 |
| Installation | The active step is indicated with `aria-current="step"`. Next validates the current basic fields; the heading receives focus when the step changes and after setup completes. Test results are labeled “Passed” or “Action required”. | 2.4.3, 3.3.1, 4.1.2 |

## Security and time limits

Accessibility features do not change server authentication, permissions, encryption or audit. Secrets are not placed in automatic status regions. A screen reader can, however, read a secret field when the user navigates to it; consider who might hear it being read aloud.

Revealed passwords and license keys are cleared and the dialog closes after **30 seconds** or on loss of focus. Edit forms retain ordinary information, but secret fields are cleared on loss of focus or after **five minutes of inactivity**. The clearing notification contains no secret values. When replacing a value, it must be entered again before Save becomes available. Metadata drafts are stored only in tab memory.

The fixed display duration is a **known accessibility limitation**, particularly for people who need more time or use screen readers. Revealing a value again requires a new request with permission checks and audit. We do not claim compliance with WCAG 2.2.1 (Timing Adjustable) or that a security exception automatically applies. A future solution for adjustable display times must undergo security review before time limits are changed.

## Automated checks

Install Node.js and the project's .NET SDK, then run from the repository root:

```powershell
npm ci
npx playwright install chromium
npm run test:security
npm run test:accessibility
```

On Linux, browser system dependencies may need to be installed with `npx playwright install --with-deps chromium`. Dependencies are locked in `package-lock.json` and needed only for development/testing.

Accessibility tests start a separate .NET test host on `localhost:58902` with synthetic data and a test identity, plus the read-only demo on `127.0.0.1:58903`. Both ports must be available. No connection to AD, real databases or a production installation is used. The test host is never included in the IIS package. Setup requests are replaced only in the test's isolated browser session; server setup authorization tests are in the C# suite.

Playwright and axe-core check relevant WCAG A/AA rules on overviews, resources, dialogs and installation steps. Additional checks cover keyboard focus, dialog tab order, Escape, form errors, 320 CSS pixel width, doubled text size, contrast mode and retention of metadata drafts when secret fields are cleared. Reports and failure traces are saved under `artifacts/accessibility-report` and `artifacts/accessibility-results`. Run the same suite for .NET, browser and interface upgrades. GitHub Actions has a separate accessibility job.

## Manual checks before release

Automated tests cannot determine whether the entire workflow is understandable or a real screen reader works well. Check the following on Windows before release:

1. Navigate using only Tab, Shift+Tab, Enter, Space and Escape. Check that focus is visible and not hidden by scrolling, especially in long dialogs and installation steps.
2. Read views and forms with NVDA or another supported screen reader. Check headings, tables, labels, help, required fields, errors, status and focus when changing views. Such manual screen reader testing has not been performed in the automated suite.
3. Try 200% text enlargement, 400% page zoom, 320 CSS pixel width, long resource names and changed text spacing. Automated reflow tests cover representative views; these additional scenarios need manual testing.
4. Check Windows high contrast mode and mobile touch navigation. Also test actual Windows sign-in, IIS and the browser's own dialogs in the installation environment.
5. Assess time limits with users who need more time. Document remaining barriers before an organization performs a formal accessibility assessment.

Report errors as GitHub issues with the view, browser, assistive technology and reproduction steps. Never attach real passwords or license keys. Any organizational accessibility statement and legal requirements need to be assessed separately for the actual installation.
