const {test, expect} = require('@playwright/test');
const AxeBuilder = require('@axe-core/playwright').default;
const fs = require('node:fs/promises');

test.beforeEach(async ({page}) => {
    // Kestrel accepts requests while the preview seeds synthetic resources.
    await expect.poll(async () => (await (await page.request.get('/api/resources')).json()).length).toBe(3);
});

test('audit resource/group filters and export share criteria and preserve accessible controls', async ({page}) => {
    await page.goto('/');
    await page.getByRole('link', {name: 'Auditlogg', exact: true}).click();
    await expect(page.getByLabel('Resurs', {exact: true})).toBeVisible();
    await page.getByLabel('Resurs', {exact: true}).selectOption({label: 'Databasplattform'});
    await page.getByLabel('Resursgrupp', {exact: true}).selectOption({label: 'IT och infrastruktur'});
    await page.getByLabel('Inkludera undergrupper', {exact: true}).uncheck();
    await page.getByLabel('Operation', {exact: true}).fill('Secret.Create');
    await page.getByLabel('Aktörens ID', {exact: true}).fill('user');
    const response = page.waitForResponse(r => r.url().includes('/api/audit?') && r.request().method() === 'GET');
    await page.getByRole('button', {name: 'Filtrera', exact: true}).click();
    const filtered = await (await response).json();
    expect(filtered).toHaveLength(2);
    expect(filtered.every(x => x.event.scope.resourceName === 'Databasplattform' && x.event.action === 'Secret.Create')).toBe(true);
    await expect(page.getByRole('status').filter({hasText: 'händelser visas'})).toContainText('2 händelser');
    const ax = await new AxeBuilder({page}).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    expect(ax.violations.map(x => x.id)).toEqual([]);
    const download = page.waitForEvent('download');
    await page.getByRole('button', {name: 'Exportera denna sida', exact: true}).click();
    const exported = JSON.parse(await fs.readFile(await (await download).path(), 'utf8'));
    expect(exported.map(x => x.Event.Id)).toEqual(filtered.map(x => x.event.id));
    await page.setViewportSize({width: 320, height: 800});
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expect(page.getByLabel('Resursgrupp', {exact: true})).toBeVisible();
});

test('group-only filtering works with subgroup checkbox and pagination', async ({page}) => {
    await page.goto('/'); await page.getByRole('link', {name: 'Auditlogg', exact: true}).click();
    await page.getByLabel('Resursgrupp', {exact: true}).selectOption({label: 'IT och infrastruktur'});
    await page.getByRole('button', {name: 'Filtrera', exact: true}).click();
    await expect(page.getByRole('table').getByText('Tjänsteövergripande / äldre händelse', {exact: true})).toHaveCount(0);
    const request = page.waitForRequest(r => r.url().includes('/api/audit?') && new URL(r.url()).searchParams.get('offset') === '200');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    const parameters = new URL((await request).url()).searchParams;
    expect(parameters.get('groupId')).toBeTruthy(); expect(parameters.get('includeSubgroups')).toBe('true');
    await expect(page.getByRole('heading', {name: 'Inga händelser', exact: true})).toBeVisible();
    await page.getByRole('button', {name: '← Föregående', exact: true}).click();
    await expect(page.getByRole('table')).toBeVisible();
});

test('corrupt scope is marked without hiding the audit table', async ({page}) => {
    await page.route('**/api/audit/targets', route => route.fulfill({json: {resources: [], groups: [], invalidEventCount: 1}}));
    await page.route('**/api/audit?*', route => route.fulfill({json: [{event: {
        timestamp: '2026-10-05T10:00:00Z', actorId: 'user', action: 'Audit.InvalidPayload', outcome: 'InvalidSignature',
        scope: {resourceName: 'Untrusted metadata', groupPath: null}}, signatureValid: false}]}));
    await page.goto('/'); await page.getByRole('link', {name: 'Auditlogg', exact: true}).click();
    await expect(page.getByRole('table')).toBeVisible();
    await expect(page.getByText('Ogiltig signatur', {exact: true})).toBeVisible();
    await expect(page.getByText('Auditkontext kan inte verifieras.', {exact: true})).toBeVisible();
    await expect(page.getByText('Untrusted metadata')).toHaveCount(0);
});

test('filter target failure leaves the audit list accessible', async ({page}) => {
    await page.route('**/api/audit/targets', route => route.fulfill({status: 503, json: {error: 'Unavailable'}}));
    await page.goto('/'); await page.getByRole('link', {name: 'Auditlogg', exact: true}).click();
    await expect(page.getByRole('table')).toBeVisible();
    await expect(page.getByLabel('Resurs', {exact: true})).toBeDisabled();
    await expect(page.getByText('Filterval kunde inte läsas. Auditlistan kan fortfarande visas.', {exact: true})).toBeVisible();
});
