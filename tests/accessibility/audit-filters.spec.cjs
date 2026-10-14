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
    await page.getByText('Avancerade filter',{exact:true}).click();
    await page.getByLabel('Operation', {exact: true}).fill('Secret.Create');
    await page.getByLabel('Person eller tjänst',{exact:true}).selectOption({label:'user'});
    const response = page.waitForResponse(r => r.url().includes('/api/audit?') && r.request().method() === 'GET');
    await page.getByRole('button', {name: 'Filtrera', exact: true}).click();
    const filtered = await (await response).json();
    expect(filtered).toHaveLength(2);
    expect(filtered.every(x => x.event.scope.resourceName === 'Databasplattform' && x.event.action === 'Secret.Create')).toBe(true);
    await expect(page.getByRole('status').filter({hasText: 'händelser visas'})).toContainText('2 händelser');
    const ax = await new AxeBuilder({page}).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    expect(ax.violations.map(x => x.id)).toEqual([]);
    const download = page.waitForEvent('download');
    // Unapplied edits must never silently change the export query.
    await page.getByLabel('Operation',{exact:true}).fill('License.Reveal');
    await expect(page.getByText('Ändrade filter är inte tillämpade. Listan och exporten använder senast tillämpade filter.',{exact:true})).toBeVisible();
    await page.getByRole('button', {name: 'Exportera denna sida', exact: true}).click();
    const exported = JSON.parse(await fs.readFile(await (await download).path(), 'utf8'));
    expect(exported.map(x => x.Event.Id)).toEqual(filtered.map(x => x.event.id));
    await page.setViewportSize({width: 320, height: 800});
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expect(page.getByLabel('Resursgrupp', {exact: true})).toBeVisible();
});

test('group-only filtering works with subgroup checkbox and pagination', async ({page}) => {
    await page.route('**/api/audit?*',async route=>{
        const response=await route.fetch(),rows=await response.json();
        await route.fulfill({response,json:new URL(route.request().url()).searchParams.get('offset')==='200'?[]:Array.from({length:200},(_,i)=>rows[i%rows.length])});
    });
    await page.goto('/'); await page.getByRole('link', {name: 'Auditlogg', exact: true}).click();
    await page.getByLabel('Resursgrupp', {exact: true}).selectOption({label: 'IT och infrastruktur'});
    await page.getByRole('button', {name: 'Filtrera', exact: true}).click();
    await expect(page.getByRole('table').getByText('Tjänsteövergripande / äldre händelse', {exact: true})).toHaveCount(0);
    const request = page.waitForRequest(r => r.url().includes('/api/audit?') && new URL(r.url()).searchParams.get('offset') === '200');
    await page.getByRole('button', {name: 'Nästa →', exact: true}).click();
    const parameters = new URL((await request).url()).searchParams;
    expect(parameters.get('groupId')).toBeTruthy(); expect(parameters.get('includeSubgroups')).toBe('true');
    await expect(page.getByRole('heading', {name: 'Inga händelser', exact: true})).toBeVisible();
    await expect(page.getByRole('button',{name:'Nästa →',exact:true})).toBeDisabled();
    await page.getByRole('button', {name: '← Föregående', exact: true}).click();
    await expect(page.getByRole('table')).toBeVisible();
});

test('recorded actors resolve names and apply exact provider and operation while boundaries disable paging',async({page})=>{
    await page.goto('/audit');
    const choice=page.getByLabel('Person eller tjänst',{exact:true});await expect(choice.locator('option')).toHaveCount(2);await expect(choice).toHaveValue('');
    await choice.selectOption('1');await page.getByLabel('Åtgärd',{exact:true}).selectOption('Secret.Create');
    const response=page.waitForResponse(r=>r.url().includes('/api/audit?'));
    await page.getByRole('button',{name:'Filtrera',exact:true}).click();const received=await response;
    expect(new URL(received.url()).searchParams.get('actorProvider')).toBe('ad');
    expect((await received.json()).every(x=>x.event.actorId==='user'&&x.event.action==='Secret.Create')).toBe(true);
    await expect(page.getByRole('button',{name:'← Föregående',exact:true})).toBeDisabled();await expect(page.getByRole('button',{name:'Nästa →',exact:true})).toBeDisabled();
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

test('export keeps displayed events after new events arrive and refresh explicitly captures a new view',async({page})=>{
 const initial=page.waitForResponse(r=>r.url().includes('/api/audit?'));await page.goto('/audit');const before=await(await initial).json();await expect(page.getByRole('table')).toBeVisible();
 // The independently audited export adds a new matching event to the source.
 expect((await page.request.get('/api/audit/export')).ok()).toBe(true);
 const download=page.waitForEvent('download');await page.getByRole('button',{name:'Exportera denna sida',exact:true}).click();const exported=JSON.parse(await fs.readFile(await(await download).path(),'utf8'));
 expect(exported.map(x=>x.Event.Id)).toEqual(before.map(x=>x.event.id));
 const refreshed=page.waitForResponse(r=>r.url().includes('/api/audit?'));await page.getByRole('button',{name:'Uppdatera auditvyn',exact:true}).click();
 expect((await(await refreshed).json()).map(x=>x.event.id)).not.toEqual(before.map(x=>x.event.id));
});

test('expired audit snapshot offers an explicit refresh instead of retrying the expired token',async({page})=>{
 let expired=false;await page.route('**/api/audit?*',route=>expired?route.fulfill({status:409,json:{error:'Expired synthetic view'}}):route.continue());
 await page.goto('/audit');await expect(page.getByRole('table')).toBeVisible();expired=true;
 await page.getByRole('button',{name:'Uppdatera auditvyn',exact:true}).click();await expect(page.getByText('Expired synthetic view',{exact:true})).toBeVisible();
 await expect(page.getByRole('button',{name:'Exportera denna sida',exact:true})).toBeDisabled();
 expired=false;await page.getByRole('button',{name:'Uppdatera auditvyn',exact:true}).last().click();await expect(page.getByText('Expired synthetic view',{exact:true})).toHaveCount(0);await expect(page.getByRole('table')).toBeVisible();
});

test('audit explains mutation phases and retains exact technical evidence and invalid records',async({page})=>{
 await page.route('**/api/audit/targets',route=>route.fulfill({json:{resources:[],groups:[],actors:[{id:'actor',provider:'ad',name:'Alex <img>'}],actions:['Resource.Create'],invalidEventCount:1}}));
 const base={timestamp:'2026-10-14T10:00:00Z',actorProvider:'ad',actorId:'actor',action:'Resource.Create',operationId:'same-operation',correlationId:'same-request',scope:null};
 await page.route('**/api/audit?*',route=>route.fulfill({json:[{event:{...base,id:'intent',phase:1,outcome:'Requested'},signatureValid:true},{event:{...base,id:'commit',phase:2,outcome:'Committed'},signatureValid:true},{event:{...base,id:'invalid',phase:2,outcome:'Success',scope:{resourceName:'Do not trust this',groupPath:[]}},signatureValid:false}]}));
 await page.goto('/audit');await page.getByLabel('Språk',{exact:true}).selectOption('en');
 await expect(page.getByLabel('Actor ID',{exact:true})).toBeHidden();
 const table=page.getByRole('table'),rows=table.getByRole('row');await expect(rows).toHaveCount(4);
 await expect(rows.nth(1).getByRole('cell').nth(1)).toHaveText('Alex <img>');await expect(rows.nth(1).getByRole('cell').nth(2)).toHaveText('Create resource');
 await expect(rows.nth(1)).toContainText('Request');await expect(rows.nth(2)).toContainText('Completed');await expect(rows.nth(2)).toContainText('Change confirmed');await expect(rows.nth(3)).toContainText('Invalid signature');
 await expect(table.locator('img')).toHaveCount(0);await expect(table).not.toContainText('Do not trust this');
 await rows.nth(1).getByText('Technical details',{exact:true}).click();await expect(rows.nth(1)).toContainText('Operation ID: same-operation');await expect(rows.nth(1)).toContainText('Operation: Resource.Create');
 await expect(page.getByText(/A change can have multiple audit records/)).toBeVisible();
});
