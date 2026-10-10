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
    let setupRequest;
    await page.route('**/api/setup/validate', route => {
        setupRequest = route.request().postDataJSON();
        return route.fulfill({json: {passed: true, checks: [{name: 'Syntetisk kontroll', passed: true, message: 'Endast webbläsartest.'}]}});
    });
    await page.route('**/api/setup/finish', route => route.fulfill({json: {}}));
    await page.goto('/setup');
    await audit(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.getByLabel('Installationskod (obligatoriskt)')).toBeFocused();
    await expect(page.getByRole('alert')).toContainText('Installationskod');
    await page.getByLabel('Installationskod (obligatoriskt)').fill('synthetic-code');
    await expect(page.getByLabel('Installationskod (obligatoriskt)')).toHaveAttribute('aria-describedby', 'setup-code-help');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Anslut databaserna', exact: true})).toBeFocused();
    await expect(page.locator('[aria-current="step"]')).toHaveText('2 · Databaser');
    await expect(page.getByLabel('Använd separat auditläsare (valfritt)', {exact: true})).not.toBeChecked();
    await expect(page.locator('#setup-readerServer')).toBeDisabled();
    await expect(page.locator('#setup-readerWindowsPassword')).toBeHidden();
    for (const field of ['vaultServer', 'writerServer']) await page.locator('#setup-' + field).fill('synthetic.example.invalid');
    await audit(page);
    await page.setViewportSize({width: 320, height: 800});
    await noPageOverflow(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    for (const field of ['adServer', 'baseDn', 'encryptionThumbprint', 'auditThumbprint', 'integrityThumbprint']) await page.locator('#setup-' + field).fill('synthetic');
    await audit(page);
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    await page.getByRole('button', {name: 'Testa anslutningar och rättigheter', exact: true}).click();
    await expect(page.getByRole('status')).toContainText('Alla kontroller passerade');
    expect(setupRequest.useSeparateAuditReader).toBe(false);
    expect(setupRequest.auditReader).toEqual(setupRequest.auditWriter);
    expect(setupRequest.auditReaderWindowsCredentials).toBeNull();
    await audit(page);
    await page.getByRole('button', {name: 'Spara och slutför installationen', exact: true}).click();
    await expect(page.getByRole('heading', {name: 'Installationen är sparad'})).toBeFocused();
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
