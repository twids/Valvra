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
    await results.selectOption('person-b'); await page.getByLabel('Access task',{exact:true}).selectOption('custom');await page.getByLabel('Read secrets', {exact:true}).check();
    const posted = page.waitForRequest(r => r.url().endsWith('/api/grants') && r.method() === 'POST');
    await page.getByRole('button', {name:'Grant', exact:true}).click();
    expect((await posted).postDataJSON()).toMatchObject({subjectId:'person-b', provider:'ad', subjectKind:0, permissions:3});
});

test('grant task presets show exact scope and preserve custom modify-only permissions',async({page})=>{
 let posted;await page.route('**/api/grants',route=>{posted=route.request().postDataJSON();return route.fulfill({status:204});});
 await page.route('**/api/directory?**',route=>route.fulfill({json:[{id:'stable-alex',provider:'ad',kind:0,name:'Alex Example',samAccountName:'alex.one'}]}));
 await grant(page);const task=page.getByLabel('Access task',{exact:true});await expect(task).toHaveValue('1');
 await page.getByLabel('Search Active Directory',{exact:true}).fill('Alex');await page.getByRole('button',{name:'Search',exact:true}).click();await page.getByLabel('Select result',{exact:true}).selectOption('stable-alex');
 await task.selectOption('7');const summary=page.getByRole('region',{name:'Review this grant',exact:true});
 await expect(summary).toContainText('Alex Example · alex.one');await expect(summary).toContainText('IT och infrastruktur / Databasplattform');
 await expect(page.getByLabel('Manage permissions',{exact:true})).toBeHidden();
 await task.selectOption('1');await task.selectOption('custom');await page.getByLabel('Modify content',{exact:true}).check();
 await page.getByRole('button',{name:'Grant',exact:true}).click();expect(posted).toMatchObject({subjectId:'stable-alex',permissions:5});
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
    await search.fill('New'); await page.getByRole('button', {name:'Search', exact:true}).click();
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

test('global role search distinguishes duplicate display names by account name and submits stable identity',async({page})=>{
    const display='Alex <img src=x onerror=alert(1)> Example';
    await page.route('**/api/settings/people?*',route=>route.fulfill({json:[
        {provider:'ad',id:'person-one',name:display,kind:0,samAccountName:'alex.one'},
        {provider:'ad',id:'person-two',name:display,kind:0,samAccountName:'alex.two'},
        {provider:'ad',id:'person-legacy',name:'Legacy Person',kind:0}
    ]}));
    let posted;
    await page.route('**/api/settings/roles',route=>route.request().method()==='POST'
        ?(posted=route.request().postDataJSON(),route.fulfill({json:{id:'synthetic'}})):route.continue());
    await page.goto('/settings');await page.getByRole('button',{name:'+ Assign user account',exact:true}).click();
    await page.getByLabel('Search for user account',{exact:true}).fill('Alex');await page.getByRole('button',{name:'Search',exact:true}).click();
    await expect(page.getByRole('button',{name:display+' · alex.one',exact:true})).toBeVisible();
    await expect(page.getByRole('button',{name:'Legacy Person · person-legacy',exact:true})).toBeVisible();
    await expect(page.getByRole('dialog').locator('img')).toHaveCount(0);
    await page.getByRole('button',{name:display+' · alex.two',exact:true}).click();
    await expect(page.getByRole('dialog').getByRole('status')).toHaveText(display+' · alex.two · ad · person-two');
    await page.getByRole('checkbox').last().check();
    await page.getByRole('button',{name:'Save',exact:true}).click();
    await expect.poll(()=>posted).toMatchObject({provider:'ad',subjectId:'person-two',kind:0,roles:128});
    expect(posted).not.toHaveProperty('samAccountName');expect(posted).not.toHaveProperty('name');
});

test('cancel and save return to permissions, revoke requires confirmation, names render as text', async ({page}) => {
    let revokes = 0;
    await page.route('**/api/access/1/*', async route => {
        const response = await route.fetch(); const access = await response.json();
        access.subjects = [{provider:'ad', id:'user', kind:0, name:'<img src=x onerror=alert(1)> Example Person'}];
        await route.fulfill({response,json:access});
    });
    await page.route('**/api/grants/*/revoke', route => { revokes++; return route.fulfill({status:204}); });
    await grant(page); await page.getByRole('button',{name:'← Back',exact:true}).click();
    const permissions = page.getByRole('dialog',{name:'Permissions · Databasplattform',exact:true});
    await expect(permissions).toBeVisible(); await expect(permissions).toContainText('Example Person');
    await expect(permissions.locator('img')).toHaveCount(0);
    await permissions.getByRole('row').filter({hasText:'Example Person'}).getByRole('button',{name:/^Revoke/}).click();
    await expect(page.getByRole('dialog',{name:'Revoke access · Databasplattform',exact:true})).toBeVisible();
    expect(revokes).toBe(0);
    await page.getByRole('button',{name:'← Back',exact:true}).click(); expect(revokes).toBe(0);
    await permissions.getByRole('row').filter({hasText:'Example Person'}).getByRole('button',{name:/^Revoke/}).click();
    await page.getByRole('button',{name:'Revoke',exact:true}).click();
    await expect(permissions).toBeVisible(); expect(revokes).toBe(1);
    await expect(permissions.getByRole('status')).toContainText('Completed and audited');
});
