const {test, expect} = require('@playwright/test');
test.use({locale: 'en-GB'});

async function grant(page) {
    await page.goto('/');
    await page.getByRole('link', {name:'Open resource · Databasplattform', exact:true}).click();
    await page.getByRole('button', {name:'Permissions', exact:true}).click();
    await page.getByRole('button', {name:'+ Grant access', exact:true}).click();
}

test('directory Enter searches, requires explicit selection, and invalidates a stale recipient', async ({page}) => {
    let writes = 0;
    await page.route('**/api/grants', route => { writes++; return route.fulfill({json:{id:'synthetic'}}); });
    await page.route('**/api/directory?**', route => route.fulfill({json:[
        {id:'person-a', provider:'ad', kind:0, name:'Alex Example'},
        {id:'person-b', provider:'ad', kind:0, name:'Alex Example'}
    ]}));
    await grant(page);
    const search = page.getByLabel('Search Active Directory', {exact:true});
    const results = page.getByLabel('Select result', {exact:true});
    await search.fill('Alex'); await search.press('Enter');
    await expect(results.locator('option')).toHaveCount(3);
    await expect(results).toHaveValue(''); expect(writes).toBe(0);
    await page.getByRole('button', {name:'Grant', exact:true}).click();
    await expect(page.getByRole('alert')).toContainText('Search for and select'); expect(writes).toBe(0);
    await results.selectOption('person-b');
    await search.fill('Another');
    await expect(results).toHaveValue(''); await expect(results.locator('option')).toHaveCount(1);
    await page.getByRole('button', {name:'Grant', exact:true}).click(); expect(writes).toBe(0);
    await search.press('Enter'); await expect(results.locator('option')).toHaveCount(3);
    await results.selectOption('person-b'); await page.getByLabel('Read secrets', {exact:true}).check();
    const posted = page.waitForRequest(r => r.url().endsWith('/api/grants') && r.method() === 'POST');
    await page.getByRole('button', {name:'Grant', exact:true}).click();
    expect((await posted).postDataJSON()).toMatchObject({subjectId:'person-b', provider:'ad', subjectKind:0, permissions:3});
});

test('late directory response cannot restore recipients from an old query', async ({page}) => {
    let release;
    const delayed = new Promise(resolve => { release = resolve; });
    await page.route('**/api/directory?**', async route => {
        const query = new URL(route.request().url()).searchParams.get('query');
        if (query === 'Old') await delayed;
        await route.fulfill({json:[{id:query, provider:'ad', kind:0, name:query}]}).catch(() => {});
    });
    await grant(page);
    const search = page.getByLabel('Search Active Directory', {exact:true});
    const oldRequest = page.waitForRequest('**/api/directory?query=Old&kind=0');
    await search.fill('Old'); await search.press('Enter'); await oldRequest;
    await search.fill('New'); await search.press('Enter');
    const results = page.getByLabel('Select result', {exact:true});
    await expect(results).toContainText('New');
    const oldResponse = page.waitForResponse('**/api/directory?query=Old&kind=0'); release(); await oldResponse;
    await expect(results).not.toContainText('Old'); await expect(results).toHaveValue('');
});

test('temporary access switches to individual users and clears group selection', async ({page}) => {
    await grant(page);
    await page.getByLabel('Type', {exact:true}).selectOption('1');
    await page.getByLabel('Validity', {exact:true}).selectOption('temporary');
    await expect(page.getByLabel('Type', {exact:true})).toHaveValue('0');
    await expect(page.getByLabel('Type', {exact:true})).toBeDisabled();
    await page.getByLabel('Validity', {exact:true}).selectOption('permanent');
    await expect(page.getByLabel('Type', {exact:true})).toBeEnabled();
});

test('global role search Enter searches and changing text clears the chosen person', async ({page}) => {
    let writes = 0;
    await page.route('**/api/settings/roles', route => route.request().method() === 'POST'
        ? (writes++, route.fulfill({json:{}})) : route.continue());
    await page.goto('/settings');
    await page.getByRole('button', {name:'+ Assign user account', exact:true}).click();
    const search = page.getByLabel('Search for user account', {exact:true});
    await search.fill('outsider'); await search.press('Enter');
    await page.getByRole('button', {name:'outsider', exact:true}).click();
    await search.fill('different'); await page.getByRole('button', {name:'Save', exact:true}).click();
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Select'); expect(writes).toBe(0);
});
