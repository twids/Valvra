const {test, expect} = require('@playwright/test');
const AxeBuilder = require('@axe-core/playwright').default;

// The web server is an isolated, loopback-only .NET host with synthetic data.
// No browser session, real directory, database or installation credentials are used.
async function audit(page) {
    const result = await new AxeBuilder({page})
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
    expect(result.violations.map(v => ({id: v.id, nodes: v.nodes.map(n => n.target)}))).toEqual([]);
}
async function openResource(page) {
    await page.goto('/');
    await page.getByRole('link', {name: 'Öppna resurs · Databasplattform', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Databasplattform', exact: true})).toBeFocused();
    await expect(page.getByRole('button', {name: 'Visa nyckel · Exempellicens'})).toBeVisible();
}
async function openLicenseEditor(page) {
    await openResource(page);
    await page.getByRole('row').filter({hasText: 'Exempellicens'}).getByRole('button', {name: /^Ändra/}).click();
    await expect(page.getByRole('dialog', {name: 'Ändra licens', exact: true})).toBeVisible();
}
async function noPageOverflow(page) {
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
}

test.beforeEach(async ({page}) => {
    await page.route('**/api/setup/draft', route => route.fulfill({json: {saved: true}}));
});

test('installation guide: incomplete drafts survive reload without passwords and save before failed tests', async ({page}) => {
    let savedDraft, saves = 0;
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    await page.route('**/api/setup/draft', route => {
        savedDraft = route.request().postDataJSON(); saves++;
        return route.fulfill({json: {saved: true}});
    });
    await page.route('**/api/setup/draft/load', route => route.fulfill({json: {draft: savedDraft}}));
    await page.route('**/api/setup/test-databases', route => {
        expect(savedDraft.fields.vaultServer).toBe('unfinished.example.test');
        return route.fulfill({json: {passed: false, checks: [{name: 'Valvdatabas', passed: false, message: 'Synthetic failure'}]}});
    });
    await page.goto('/setup');
    await page.locator('#setup-code').fill('a'.repeat(64));
    await page.getByRole('button', {name: '2 · Databaser', exact: true}).click();
    await page.locator('#setup-vaultServer').fill('unfinished.example.test');
    await page.locator('#setup-vaultAuthentication').selectOption('Password');
    await page.locator('#setup-vaultUsername').fill('synthetic-user');
    await page.locator('#setup-vaultPassword').fill('must-never-be-in-draft');
    await page.locator('#setup-provision').check();
    await page.locator('#setup-vaultAdminPassword').fill('temporary-secret');
    await page.getByRole('button', {name: 'Spara utkast', exact: true}).click();
    await expect(page.getByRole('status')).toContainText('Utkastet är sparat');
    expect(savedDraft.step).toBe(1);
    expect(savedDraft.fields.vaultServer).toBe('unfinished.example.test');
    expect(Object.keys(savedDraft.fields).some(key => /password|code/i.test(key))).toBe(false);
    expect(JSON.stringify(savedDraft)).not.toContain('must-never');
    await page.reload();
    await page.locator('#setup-code').fill('a'.repeat(64));
    await page.getByRole('button', {name: 'Läs in sparat utkast', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Anslut databaserna', exact: true})).toBeFocused();
    await expect(page.locator('#setup-vaultServer')).toHaveValue('unfinished.example.test');
    await expect(page.locator('#setup-vaultUsername')).toHaveValue('synthetic-user');
    await expect(page.locator('#setup-vaultPassword')).toBeEnabled();
    await expect(page.locator('#setup-vaultPassword')).toHaveValue('');
    await expect(page.locator('#setup-vaultAdminPassword')).toBeVisible();
    await expect(page.locator('#setup-vaultAdminPassword')).toHaveValue('');
    await expect(page.locator('#setup-code')).toHaveValue('a'.repeat(64));
    await audit(page);
    await page.locator('#setup-provision').uncheck();
    await page.locator('#setup-writerServer').fill('audit.example.test');
    await page.getByRole('button', {name: 'Testa databaser', exact: true}).click();
    await expect(page.locator('#setup-database-results')).toContainText('Synthetic failure');
    expect(saves).toBe(2);
    await page.locator('[data-language-picker]').selectOption('en');
    await expect(page.getByRole('button', {name: 'Save draft', exact: true})).toBeVisible();
    await page.getByRole('button', {name: '5 · Review and save', exact: true}).click();
    await expect(page.getByRole('button', {name: 'Save and finish setup', exact: true})).toBeDisabled();
});

test('resource overview: WCAG rules, first-tab skip link and visible focus', async ({page}) => {
    await page.goto('/');
    await expect(page.getByRole('link', {name: 'Öppna resurs · Databasplattform'})).toBeVisible();
    await audit(page);
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', {name: 'Hoppa till huvudinnehållet'});
    await expect(skip).toBeFocused();
    expect(await skip.evaluate(el => el.getBoundingClientRect().top)).toBeGreaterThanOrEqual(0);
    expect(await skip.evaluate(el => getComputedStyle(el).outlineStyle)).toBe('solid');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('main')).toBeFocused();
});

for (const [menu, title] of [['Resursgrupper', 'Resursgrupper'], ['Licensöversikt', 'Licensöversikt'], ['Auditlogg', 'Auditlogg']]) {
    test(`${menu}: WCAG rules, navigation state and heading focus`, async ({page}) => {
        await page.goto('/');
        const nav = page.getByRole('navigation', {name: 'Huvudmeny'}).getByRole('link', {name: menu, exact: true});
        await nav.click();
        await expect(page.getByRole('heading', {name: title, level: 1})).toBeFocused();
        await expect(nav).toHaveAttribute('aria-current', 'page');
        await expect(page).toHaveTitle(`${title} · Valvra`);
        await audit(page);
    });
}

test('resource tables and password/license dialogs satisfy automated WCAG rules', async ({page}) => {
    await openResource(page);
    await audit(page);
    const actions = [
        'Visa · Administratörskonto', 'Visa nyckel · Exempellicens',
        '+ Lägg till lösenord', '+ Lägg till licens',
        'Historik · Administratörskonto', 'Historik · Exempellicens',
        'Tilldelningar · Exempellicens', 'Papperskorg', 'Behörigheter'
    ];
    for (const name of actions) {
        await page.getByRole('button', {name, exact: true}).click();
        await expect(page.getByRole('dialog')).toBeVisible();
        await expect(page.locator('#dialog-title')).toBeFocused();
        await audit(page);
        if (name === 'Behörigheter') {
            await page.getByRole('button', {name: '+ Tilldela åtkomst', exact: true}).click();
            await audit(page);
        }
        await page.keyboard.press('Escape');
        await expect(page.getByRole('dialog')).not.toBeVisible();
    }
});

test('license editor: keyboard containment, Escape and focus return', async ({page}) => {
    await openLicenseEditor(page);
    await audit(page);
    for (let i = 0; i < 24; i++) {
        await page.keyboard.press(i < 12 ? 'Tab' : 'Shift+Tab');
        expect(await page.evaluate(() => document.querySelector('#dialog').contains(document.activeElement))).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(page.getByRole('row').filter({hasText: 'Exempellicens'}).getByRole('button', {name: /^Ändra/})).toBeFocused();
});

test('required fields and server errors have visible and accessible feedback', async ({page}) => {
    await openLicenseEditor(page);
    const product = page.getByLabel('Produkt (obligatoriskt)', {exact: true});
    await product.fill('');
    await page.getByRole('button', {name: 'Spara', exact: true}).click();
    await expect(product).toBeFocused();
    await expect(product).toHaveAttribute('aria-invalid', 'true');
    await expect(page.locator('#dialog-error')).toContainText('Produkt (obligatoriskt)');
    await product.fill('Exempellicens');
    await expect(product).not.toHaveAttribute('aria-invalid', 'true');
    await page.route('**/api/licenses/*/update', route => route.fulfill({status: 409, json: {error: 'Posten har ändrats. Uppdatera och försök igen.'}}));
    await page.getByRole('button', {name: 'Spara', exact: true}).click();
    await expect(page.getByRole('alert')).toContainText('Posten har ändrats');
    await audit(page);
});

test('saving returns focus to the page heading when the original row was replaced', async ({page}) => {
    await openLicenseEditor(page);
    await page.getByRole('button', {name: 'Spara', exact: true}).click();
    await expect(page.getByRole('dialog')).not.toBeVisible();
    await expect(page.getByRole('heading', {name: 'Databasplattform', exact: true})).toBeFocused();
    await expect(page.locator('#notice')).toContainText('sparats och auditerats');
});

test('focus-loss security retains metadata, clears secret fields and avoids live-region disclosure', async ({page}) => {
    await openLicenseEditor(page);
    await page.getByLabel('Produkt (obligatoriskt)').fill('Utkast');
    await page.getByLabel('Licensnyckel och hemliga anteckningar', {exact: true}).selectOption('1');
    await page.getByLabel('Ny licensnyckel', {exact: true}).fill('synthetic-secret-only');
    await page.evaluate(() => window.dispatchEvent(new Event('blur')));
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByLabel('Produkt (obligatoriskt)')).toHaveValue('Utkast');
    await expect(page.getByLabel('Ny licensnyckel', {exact: true})).toHaveValue('');
    await expect(page.getByRole('button', {name: 'Spara', exact: true})).toBeDisabled();
    expect((await page.locator('[role="alert"],[role="status"],[aria-live]').allTextContents()).join(' ')).not.toContain('synthetic-secret-only');
});

test('320px viewport and 200% text size preserve content without page-wide horizontal scrolling', async ({page}) => {
    await page.setViewportSize({width: 320, height: 800});
    await page.goto('/');
    await expect(page.getByRole('link', {name: 'Öppna resurs · Databasplattform'})).toBeVisible();
    await noPageOverflow(page);
    await audit(page);
    await openLicenseEditor(page);
    await noPageOverflow(page);
    await audit(page);
    await page.keyboard.press('Escape');
    // Independent equivalent of text-only zoom, rather than scaling a screenshot.
    await page.route('**/css/site.css', async route => {
        const original = await route.fetch();
        await route.fulfill({response: original, body: await original.text() + '\nhtml{font-size:200%}'});
    });
    await openLicenseEditor(page);
    expect(await page.locator('body').evaluate(el => getComputedStyle(el).fontSize)).toBe('32px');
    await expect(page.getByRole('button', {name: 'Stäng dialog'})).toBeVisible();
    await noPageOverflow(page);
});

test('forced colors and reduced motion keep focus and controls perceivable', async ({page}) => {
    await page.emulateMedia({forcedColors: 'active', reducedMotion: 'reduce'});
    await page.goto('/');
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', {name: 'Hoppa till huvudinnehållet'});
    await expect(skip).toBeFocused();
    expect(await skip.evaluate(el => getComputedStyle(el).outlineWidth)).toBe('3px');
    await page.keyboard.press('Enter');
    await openLicenseEditor(page);
    await audit(page);
});

test('installation guide: labels, help, validation, step focus and completion', async ({page}) => {
    // Expose the setup UI only in this isolated browser test. No real installation
    // request is sent; the server's setup authorization is covered by C# tests.
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    let setupRequest, validationFails = true, finishFails = true;
    const originalError = 'LdapException: Invalid credentials\nLDAP error code: 49\n80090308: data 52e <script>window.setupErrorExecuted=true</script> ' + 'diagnostic'.repeat(35);
    const failedCheck = {name: 'Active Directory', passed: false, message: 'Kontrollera LDAPS-certifikat, katalogläsrätt och ditt personkonto.', details: originalError};
    await page.route('**/api/setup/validate', route => {
        setupRequest = route.request().postDataJSON();
        if (validationFails) return route.fulfill({json: {passed: false, checks: [failedCheck]}});
        return route.fulfill({json: {passed: true, checks: [{name: 'Syntetisk kontroll', passed: true, message: 'Endast webbläsartest.'}]}});
    });
    await page.route('**/api/setup/finish', route => finishFails
        ? route.fulfill({status: 400, json: {error: 'Installationskontrollerna måste passera innan konfigurationen sparas.', checks: [failedCheck]}})
        : route.fulfill({json: {}}));
    await page.goto('/setup');
    const preparation = page.getByRole('link', {name: 'Förbered servern och kör installationsskriptet', exact: true});
    await expect(preparation).toHaveAttribute('href', 'https://github.com/twids/Valvra/blob/master/README.md#installation--windows-and-iis');
    await expect(preparation).toHaveAttribute('target', '_blank');
    await expect(preparation).toHaveAttribute('rel', 'noopener noreferrer');
    await audit(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.getByLabel('Installationskod (obligatoriskt)')).toBeFocused();
    await expect(page.getByRole('alert')).toContainText('Installationskod');
    await page.getByLabel('Installationskod (obligatoriskt)').fill('synthetic-code');
    await expect(page.getByLabel('Installationskod (obligatoriskt)')).toHaveAttribute('aria-describedby', 'setup-code-help');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Anslut databaserna', exact: true})).toBeFocused();
    await expect(page.locator('[aria-current="step"]')).toHaveText('2 · Databaser');
    const databaseGuide = page.getByRole('link', {name: 'Databaser, Windows-inloggning och databasroller', exact: true});
    await expect(databaseGuide).toHaveAttribute('href', 'https://github.com/twids/Valvra/blob/master/docs/DATABASES.md');
    await page.locator('[data-language-picker]').selectOption('en');
    await expect(page.getByRole('link', {name: 'Databases, Windows sign-in and database roles', exact: true})).toBeVisible();
    await expect(page.locator('#setup-fields').getByText('The guides are in English on GitHub and open in a new tab. Your entries stay here.', {exact: true}).filter({visible: true})).toBeVisible();
    await expect(page.locator('#setup-code')).toHaveValue('synthetic-code');
    await page.locator('[data-language-picker]').selectOption('sv');
    await expect(page.getByLabel('Använd separat auditläsare (valfritt)', {exact: true})).not.toBeChecked();
    await expect(page.locator('#setup-readerServer')).toBeDisabled();
    await expect(page.locator('#setup-readerWindowsPassword')).toBeHidden();
    for (const prefix of ['vault', 'writer']) {
        const port = page.locator('#setup-' + prefix + 'Port');
        await expect(port).toBeHidden();
        await expect(page.locator('label[for="setup-' + prefix + 'Port"]')).toBeHidden();
        await expect(page.locator('#setup-' + prefix + 'Server-help')).toContainText('server,port');
        await page.locator('#setup-' + prefix + 'Provider').selectOption('PostgreSql');
        await expect(port).toBeVisible();
        await expect(port).toBeEnabled();
        await expect(port).toHaveValue('5432');
        await port.fill('5433');
        await page.locator('#setup-' + prefix + 'Provider').selectOption('SqlServer');
        await expect(port).toBeHidden();
        await expect(port).toBeDisabled();
        await page.locator('#setup-' + prefix + 'Provider').selectOption('PostgreSql');
        await expect(port).toHaveValue('5433');
        await page.locator('#setup-' + prefix + 'Provider').selectOption('SqlServer');
    }
    for (const field of ['vaultServer', 'writerServer']) await page.locator('#setup-' + field).fill('synthetic.example.invalid');
    await audit(page);
    await page.setViewportSize({width: 320, height: 800});
    await noPageOverflow(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Identitet och Active Directory', exact: true})).toBeFocused();
    await expect(page.locator('#setup-encryptionThumbprint')).toBeHidden();
    for (const field of ['adServer', 'baseDn']) await page.locator('#setup-' + field).fill('synthetic');
    await audit(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Certifikat och nyckelskydd', exact: true})).toBeFocused();
    await expect(page.locator('#setup-adServer')).toBeHidden();
    for (const field of ['encryptionThumbprint', 'auditThumbprint', 'integrityThumbprint']) await page.locator('#setup-' + field).fill('synthetic');
    await expect(page.getByRole('link', {name: 'Installationsskript och certifikat', exact: true})).toHaveAttribute('href', 'https://github.com/twids/Valvra/blob/master/README.md#2-publish-and-install');
    await audit(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.locator('.setup-review')).toContainText('synthetic.example.invalid');
    await expect(page.locator('.setup-review')).not.toContainText('synthetic-code');
    await page.getByRole('button', {name: 'Ändra Identitet och Active Directory', exact: true}).click();
    await expect(page.locator('#setup-adServer')).toHaveValue('synthetic');
    await page.getByRole('button', {name: '5 · Kontrollera och spara', exact: true}).click();
    await page.getByRole('button', {name: 'Testa anslutningar och rättigheter', exact: true}).click();
    await expect(page.locator('.setup-error-details')).toHaveText(originalError);
    await expect(page.locator('.setup-check').getByRole('button', {name: 'Ändra Identitet och Active Directory', exact: true})).toBeVisible();
    await expect(page.getByRole('button', {name: 'Spara och slutför installationen', exact: true})).toBeDisabled();
    expect(await page.evaluate(() => window.setupErrorExecuted)).toBeUndefined();
    await noPageOverflow(page);
    await page.locator('[data-language-picker]').selectOption('en');
    await expect(page.locator('.setup-error-details')).toHaveText(originalError);
    await page.locator('[data-language-picker]').selectOption('sv');
    await audit(page);
    validationFails = false;
    await page.getByRole('button', {name: 'Testa anslutningar och rättigheter', exact: true}).click();
    await expect(page.locator('.setup-error-details')).toHaveCount(0);
    await expect(page.getByRole('link', {name: 'Backup och återställning', exact: true})).toHaveAttribute('href', 'https://github.com/twids/Valvra/blob/master/docs/OPERATIONS.md#what-must-be-backed-up');
    await expect(page.getByRole('status')).toContainText('Alla kontroller passerade');
    expect(setupRequest.useSeparateAuditReader).toBe(false);
    expect(setupRequest.auditReader).toEqual(setupRequest.auditWriter);
    expect(setupRequest.auditReaderWindowsCredentials).toBeNull();
    await audit(page);
    await page.getByRole('button', {name: 'Spara och slutför installationen', exact: true}).click();
    await expect(page.locator('.setup-error-details')).toHaveText(originalError);
    await expect(page.getByRole('button', {name: 'Spara och slutför installationen', exact: true})).toBeDisabled();
    finishFails = false;
    await page.getByRole('button', {name: 'Testa anslutningar och rättigheter', exact: true}).click();
    await page.getByRole('button', {name: 'Spara och slutför installationen', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Installationen är sparad'})).toBeFocused();
    await audit(page);
});

test('installation guide: edits invalidate validation, including a pending response', async ({page}) => {
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    let release, pending, requests = 0;
    await page.route('**/api/setup/validate', async route => {
        pending = new Promise(resolve => { release = resolve; });
        requests++;
        await pending;
        await route.fulfill({json: {passed: true, checks: [{name: 'Synthetic check', passed: true, message: 'Synthetic only'}]}});
    });
    await page.goto('/setup');
    await page.locator('#setup-code').fill('synthetic-code');
    await page.getByRole('button', {name: '2 · Databaser', exact: true}).click();
    for (const key of ['vaultServer', 'writerServer']) await page.locator('#setup-' + key).fill('synthetic.example.invalid');
    await page.getByRole('button', {name: '3 · Identitet', exact: true}).click();
    for (const key of ['adServer', 'baseDn']) await page.locator('#setup-' + key).fill('synthetic');
    await page.locator('[data-language-picker]').selectOption('en');
    await expect(page.getByRole('heading', {name: 'Identity and Active Directory', exact: true})).toBeVisible();
    await page.screenshot({path: 'artifacts/setup-identity.png', fullPage: true});
    await page.getByRole('button', {name: '4 · Certificates', exact: true}).click();
    for (const key of ['encryptionThumbprint', 'auditThumbprint', 'integrityThumbprint']) await page.locator('#setup-' + key).fill('synthetic-' + key);
    await page.screenshot({path: 'artifacts/setup-certificates.png', fullPage: true});
    await page.getByRole('button', {name: '5 · Review and save', exact: true}).click();
    await page.screenshot({path: 'artifacts/setup-review.png', fullPage: true});
    const testButton = page.getByRole('button', {name: 'Test connections and permissions', exact: true});
    const save = page.getByRole('button', {name: 'Save and finish setup', exact: true});
    await testButton.click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await page.getByRole('button', {name: '2 · Databases', exact: true}).click();
    await page.locator('#setup-vaultProvider').selectOption('PostgreSql');
    release();
    await page.getByRole('button', {name: '5 · Review and save', exact: true}).click();
    await expect(testButton).toBeEnabled();
    await expect(save).toBeDisabled();
    await expect(page.locator('.setup-check')).toHaveCount(0);
    await testButton.click();
    await expect.poll(() => requests).toBe(2);
    release();
    await expect(save).toBeEnabled();
    await page.getByRole('button', {name: '3 · Identity', exact: true}).click();
    await page.locator('#setup-adServer').fill('changed.example.invalid');
    await page.getByRole('button', {name: '5 · Review and save', exact: true}).click();
    await expect(save).toBeDisabled();
    await expect(page.locator('.setup-review')).toContainText('changed.example.invalid');
    await expect(page.locator('.setup-check')).toHaveCount(0);
});

test('installation guide: database test works before AD and certificates and clears stale results', async ({page}) => {
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    let payload, succeed = false;
    await page.route('**/api/setup/test-databases', route => {
        payload = route.request().postDataJSON();
        return route.fulfill({json: {passed: succeed, checks: [
            {nameKey: 'Valvdatabas', passed: true, messageKey: 'Anslutning och autentisering lyckades.'},
            {nameKey: 'Auditdatabas (SELECT och INSERT)', passed: succeed,
                messageKey: succeed ? 'Anslutning och autentisering lyckades.' : 'Kunde inte ansluta. Kontrollera server, databas, TLS och kontots åtkomst.',
                details: succeed ? null : 'Synthetic TLS failure <script>window.databaseErrorExecuted=true</script>'}
        ]}});
    });
    await page.goto('/setup');
    await page.locator('#setup-code').fill('synthetic-code');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await page.locator('[data-language-picker]').selectOption('en');
    for (const prefix of ['vault', 'writer']) await page.locator('#setup-' + prefix + 'Server').fill('synthetic.example.invalid');
    await page.getByRole('button', {name: 'Test database', exact: true}).click();
    const results = page.locator('#setup-database-results');
    await expect(results.locator('.setup-check.passed')).toHaveCount(1);
    await expect(results.locator('.setup-check.failed')).toHaveCount(1);
    await expect(results).toContainText('Could not connect.');
    await expect(results.locator('pre')).toContainText('<script>');
    expect(await page.evaluate(() => window.databaseErrorExecuted)).toBeUndefined();
    expect(payload.vault.authentication).toBe('Windows');
    expect(payload.vault.username).toBe(''); expect(payload.vault.password).toBe('');
    expect(payload.useSeparateAuditReader).toBe(false);
    expect(payload).not.toHaveProperty('activeDirectory'); expect(payload).not.toHaveProperty('keyProtection');
    await audit(page);
    await page.locator('#setup-writerAuthentication').selectOption('Password');
    await expect(page.locator('.setup-check')).toHaveCount(0);
    await page.locator('#setup-writerUsername').fill('synthetic-account');
    await page.locator('#setup-writerPassword').fill('synthetic-password');
    succeed = true;
    await page.getByRole('button', {name: 'Test database', exact: true}).click();
    await expect(results.locator('.setup-check.passed')).toHaveCount(2);
    expect(payload.auditWriter.username).toBe('synthetic-account');
    expect(payload.auditWriter.password).toBe('synthetic-password');
    await expect(page.locator('#setup-adServer')).toHaveValue('');
    await page.getByRole('button', {name: 'Next →', exact: true}).click();
    for (const field of ['adServer', 'baseDn']) await page.locator('#setup-' + field).fill('synthetic');
    await page.getByRole('button', {name: 'Next →', exact: true}).click();
    for (const field of ['encryptionThumbprint', 'auditThumbprint', 'integrityThumbprint']) await page.locator('#setup-' + field).fill('synthetic');
    await page.getByRole('button', {name: 'Next →', exact: true}).click();
    await expect(page.getByRole('button', {name: 'Save and finish setup', exact: true})).toBeDisabled();
});

test('installation guide: advanced database options are explicit and follow the provider', async ({page}) => {
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    let payload;
    await page.route('**/api/setup/test-databases', route => {
        payload = route.request().postDataJSON();
        return route.fulfill({json: {checks: []}});
    });
    await page.goto('/setup');
    await page.locator('#setup-code').fill('synthetic-code');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await page.locator('[data-language-picker]').selectOption('en');
    for (const prefix of ['vault', 'writer']) await page.locator('#setup-' + prefix + 'Server').fill('sql.example.invalid');
    await page.getByRole('button', {name: 'Test database', exact: true}).click();
    await expect.poll(() => payload?.vault.trustServerCertificate).toBe(false);
    expect(payload.vault.multiSubnetFailover).toBe(false);
    expect(payload.vault.connectTimeoutSeconds).toBe(10);
    const advanced = page.locator('#setup-vaultTrustServerCertificate').locator('xpath=ancestor::details');
    await advanced.locator('summary').click();
    await page.locator('#setup-vaultTrustServerCertificate').check();
    await page.locator('#setup-vaultMultiSubnetFailover').check();
    await page.locator('#setup-vaultConnectTimeoutSeconds').fill('45');
    await page.locator('#setup-provision').check();
    for (const prefix of ['vaultAdmin', 'auditAdmin']) await page.locator('#setup-' + prefix + 'Authentication').selectOption('Windows');
    await page.getByRole('button', {name: 'Test database', exact: true}).click();
    await expect.poll(() => payload?.vault.trustServerCertificate).toBe(true);
    expect(payload.vault.multiSubnetFailover).toBe(true);
    expect(payload.vault.connectTimeoutSeconds).toBe(45);
    expect(payload.vaultProvisioning.trustServerCertificate).toBe(true);
    expect(payload.vaultProvisioning.multiSubnetFailover).toBe(true);
    expect(payload.vaultProvisioning.connectTimeoutSeconds).toBe(45);
    expect(payload.auditReader).toEqual(payload.auditWriter);
    await audit(page);
    await page.locator('#setup-vaultProvider').selectOption('PostgreSql');
    await expect(page.locator('#setup-vaultTrustServerCertificate')).toBeHidden();
    await expect(page.locator('#setup-vaultTrustServerCertificate')).not.toBeChecked();
    await page.getByRole('button', {name: 'Test database', exact: true}).click();
    await expect.poll(() => payload?.vault.provider).toBe('PostgreSql');
    expect(payload.vault.trustServerCertificate).toBe(false);
    expect(payload.vault.multiSubnetFailover).toBe(false);
    await page.locator('#setup-vaultProvider').selectOption('SqlServer');
    await expect(page.locator('#setup-vaultTrustServerCertificate')).toBeVisible();
    await expect(page.locator('#setup-vaultTrustServerCertificate')).not.toBeChecked();
});

test('installation guide: current service account hides credentials for runtime and schema setup', async ({page}) => {
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    await page.goto('/setup');
    await page.locator('#setup-code').fill('synthetic');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await page.locator('[data-language-picker]').selectOption('en');
    for (const prefix of ['vault', 'writer']) {
        const authentication = page.locator('#setup-' + prefix + 'Authentication');
        await expect(authentication.locator('option:checked')).toHaveText('Current service account (Integrated Security)');
        await expect(page.locator('#setup-' + prefix + 'Username')).toBeHidden();
        await expect(page.locator('#setup-' + prefix + 'Password')).toBeHidden();
    }
    await page.locator('#setup-provision').check();
    for (const prefix of ['vault', 'writer', 'vaultAdmin', 'auditAdmin']) {
        const authentication = page.locator('#setup-' + prefix + 'Authentication');
        const username = page.locator('#setup-' + prefix + 'Username');
        const password = page.locator('#setup-' + prefix + 'Password');
        await authentication.selectOption('Password');
        await username.fill('synthetic-user');
        await password.fill('synthetic-password');
        await authentication.selectOption('Windows');
        for (const input of [username, password]) {
            await expect(input).toBeHidden();
            await expect(input).toBeDisabled();
            await expect(input).toHaveValue('');
            await expect(page.locator('label[for="' + await input.getAttribute('id') + '"]')).toBeHidden();
        }
    }
    // PostgreSQL still allows an explicit database role with integrated authentication.
    await page.locator('#setup-vaultProvider').selectOption('PostgreSql');
    for (const prefix of ['vault', 'vaultAdmin']) {
        await expect(page.locator('#setup-' + prefix + 'Username')).toBeVisible();
        await expect(page.locator('#setup-' + prefix + 'Username')).toBeEnabled();
        await expect(page.locator('#setup-' + prefix + 'Password')).toBeHidden();
    }
    await page.locator('#setup-vaultProvider').selectOption('SqlServer');
    await page.locator('#setup-separateReader').check();
    await expect(page.locator('#setup-readerUsername')).toBeDisabled();
    await expect(page.locator('#setup-readerUsername')).toBeHidden();
    await audit(page);
});

test('installation guide: preparation remains available before a setup code is generated', async ({page}) => {
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: false, installed: false}}));
    await page.goto('/setup');
    await expect(page.locator('#setup-form')).toBeHidden();
    await expect(page.getByRole('link', {name: 'Förbered servern och kör installationsskriptet', exact: true})).toBeVisible();
    await audit(page);
});

test('installation guide: separate reader is optional and clears its password when disabled', async ({page}) => {
    await page.route('**/api/setup/status', route => route.fulfill({json: {canConfigure: true, csrfToken: 'synthetic'}}));
    await page.goto('/setup');
    await page.locator('#setup-code').fill('synthetic');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    const choice = page.getByLabel('Använd separat auditläsare (valfritt)', {exact: true});
    await choice.check();
    await expect(page.locator('#setup-readerServer')).toBeEnabled();
    await expect(page.locator('#setup-readerServer')).toHaveAttribute('required', '');
    await expect(page.locator('#setup-readerPassword')).toBeDisabled();
    await expect(page.locator('#setup-readerPort')).toBeHidden();
    await page.locator('#setup-readerProvider').selectOption('PostgreSql');
    await expect(page.locator('#setup-readerPort')).toBeVisible();
    await expect(page.locator('#setup-readerPort')).toBeEnabled();
    await page.locator('#setup-readerWindowsPassword').fill('synthetic-not-a-real-password');
    await audit(page);
    await choice.uncheck();
    await expect(page.locator('#setup-readerServer')).toBeDisabled();
    await expect(page.locator('#setup-readerWindowsPassword')).toHaveValue('');
    await expect(page.locator('#setup-readerWindowsPassword')).toBeHidden();
    await audit(page);
});

test('synthetic demo uses the same accessible navigation, tables and named reveal dialogs', async ({page}) => {
    await page.goto('http://127.0.0.1:58903/');
    await expect(page.getByRole('link', {name: 'Öppna resurs · Exempeldatabasserver'})).toBeVisible();
    await audit(page);
    await page.getByRole('link', {name: 'Öppna resurs · Exempeldatabasserver'}).click();
    await expect(page.getByRole('heading', {name: 'Exempeldatabasserver', exact: true})).toBeFocused();
    await audit(page);
    await page.getByRole('button', {name: 'Visa nyckel · Exempelprogram', exact: true}).click();
    await expect(page.getByRole('dialog', {name: 'Licensnyckel · Exempelprogram'})).toBeVisible();
    await audit(page);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', {name: 'Visa nyckel · Exempelprogram', exact: true})).toBeFocused();
});
