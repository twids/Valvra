const {test,expect} = require('@playwright/test');
const AxeBuilder = require('@axe-core/playwright').default;
async function data(page) {
    await expect.poll(async()=> (await (await page.request.get('/api/resources')).json()).length).toBe(3);
    const groups=await (await page.request.get('/api/groups')).json();
    const resources=await (await page.request.get('/api/resources')).json();
    return {group:groups.find(x=>x.name==='IT och infrastruktur'),resource:resources.find(x=>x.name==='Databasplattform')};
}
async function focused(page,name) { await expect(page.getByRole('heading',{name,level:1,exact:true})).toBeFocused(); }
async function visible(page,name) { await expect(page.getByRole('heading',{name,level:1,exact:true})).toBeVisible(); }
async function audit(page) {
    const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(x=>x.id)).toEqual([]);
}
test('browser back/forward traverses every main view, group and resource context',async({page})=>{
    const {group,resource}=await data(page); await page.goto('/');
    await expect(page).toHaveURL('/resources');
    await page.getByRole('link',{name:'Resursgrupper',exact:true}).click();
    await page.getByRole('link',{name:'Öppna grupp · IT och infrastruktur',exact:true}).click();
    await expect(page).toHaveURL('/groups/'+group.id);
    await page.getByRole('link',{name:'Öppna resurs · Databasplattform',exact:true}).click();
    await expect(page).toHaveURL('/groups/'+group.id+'/resources/'+resource.id);
    await page.getByRole('link',{name:'Licensöversikt',exact:true}).click(); await focused(page,'Licensöversikt');
    await page.getByRole('link',{name:'Inställningar',exact:true}).click(); await focused(page,'Inställningar');
    await page.getByRole('link',{name:'Auditlogg',exact:true}).click(); await focused(page,'Auditlogg');
    for(const title of ['Inställningar','Licensöversikt','Databasplattform','IT och infrastruktur','Resursgrupper','Dina resurser']) {
        await page.goBack(); await focused(page,title);
    }
    for(const title of ['Resursgrupper','IT och infrastruktur','Databasplattform','Licensöversikt','Inställningar','Auditlogg']) {
        await page.goForward(); await focused(page,title);
    }
    await audit(page);
});
test('direct links and reload serve the authenticated shell for all views',async({page})=>{
    const {group,resource}=await data(page);
    for(const [path,title] of [['/resources','Dina resurser'],['/groups','Resursgrupper'],
        ['/groups/'+group.id,'IT och infrastruktur'],['/resources/'+resource.id,'Databasplattform'],
        ['/groups/'+group.id+'/resources/'+resource.id,'Databasplattform'],['/licenses','Licensöversikt'],['/audit','Auditlogg'],['/settings','Inställningar']]) {
        const response=await page.goto(path); expect(response.status()).toBe(200); await visible(page,title);
        await page.reload(); await visible(page,title); await expect(page).toHaveURL(path);
    }
});
test('navigation clicked during initial identity verification is queued without reloading',async({page})=>{
    await data(page); let release; const pending=new Promise(r=>release=r); let sessions=0;
    await page.route('**/api/session',async route=>{sessions++; const response=await route.fetch(); await pending; await route.fulfill({response});});
    await page.goto('/');
    await page.getByRole('link',{name:'Licensöversikt',exact:true}).click(); release();
    await expect(page).toHaveURL('/licenses'); await focused(page,'Licensöversikt');
    expect(sessions).toBe(1);
    await page.goBack(); await focused(page,'Dina resurser');
});
test('resource and group filters are restored from memory during history traversal',async({page})=>{
    const {group}=await data(page); await page.goto('/resources');
    await page.getByLabel('Sök resurser',{exact:true}).fill('Databas');
    await page.getByLabel('Resursgrupp',{exact:true}).selectOption(group.id);
    await page.getByLabel('Inkludera undergrupper',{exact:true}).uncheck();
    await page.getByRole('link',{name:'Öppna resurs · Databasplattform',exact:true}).click();
    await page.goBack(); await focused(page,'Dina resurser');
    await expect(page.getByLabel('Sök resurser',{exact:true})).toHaveValue('Databas');
    await expect(page.getByLabel('Resursgrupp',{exact:true})).toHaveValue(group.id);
    await expect(page.getByLabel('Inkludera undergrupper',{exact:true})).not.toBeChecked();
    await page.goto('/groups/'+group.id);
    await page.getByLabel('Sök resurser',{exact:true}).fill('Windows');
    await page.getByLabel('Inkludera undergrupper',{exact:true}).uncheck();
    await page.getByRole('link',{name:'Öppna resurs · Windows-servrar',exact:true}).click();
    await page.goBack(); await focused(page,'IT och infrastruktur');
    await expect(page.getByLabel('Sök resurser',{exact:true})).toHaveValue('Windows');
    await expect(page.getByLabel('Inkludera undergrupper',{exact:true})).not.toBeChecked();
    expect(page.url()).not.toContain('Windows');
});
test('audit filters and paging return with the audit history entry',async({page})=>{
    await data(page); await page.goto('/audit');
    await page.getByLabel('Aktörens ID',{exact:true}).fill('synthetic-actor');
    await page.getByLabel('Operation',{exact:true}).fill('Secret.Read');
    await page.getByRole('button',{name:'Filtrera',exact:true}).click();
    await page.getByRole('button',{name:'Nästa →',exact:true}).click();
    await expect(page.getByRole('status').filter({hasText:'händelser visas'})).toContainText('sida 2');
    await page.getByRole('link',{name:'Resurser',exact:true}).click();
    await page.goBack(); await focused(page,'Auditlogg');
    await expect(page.getByLabel('Aktörens ID',{exact:true})).toHaveValue('synthetic-actor');
    await expect(page.getByLabel('Operation',{exact:true})).toHaveValue('Secret.Read');
    await expect(page.getByRole('status').filter({hasText:'händelser visas'})).toContainText('sida 2');
    expect(page.url()).not.toContain('synthetic-actor');
});
test('history closes and clears sensitive dialogs without replaying reveals or drafts',async({page})=>{
    await data(page); await page.goto('/groups');
    await page.getByRole('link',{name:'Öppna grupp · IT och infrastruktur',exact:true}).click();
    await page.getByRole('link',{name:'Öppna resurs · Databasplattform',exact:true}).click();
    let reveals=0; page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
    await page.getByRole('button',{name:'Visa nyckel · Exempellicens',exact:true}).click();
    await expect(page.getByRole('dialog')).toBeVisible(); expect(reveals).toBe(1);
    await page.goBack(); await focused(page,'IT och infrastruktur');
    await expect(page.getByRole('dialog')).toBeHidden(); await expect(page.locator('#dialog-content')).toBeEmpty();
    await page.goForward(); await focused(page,'Databasplattform'); expect(reveals).toBe(1);
    await page.getByRole('button',{name:'Ändra · Exempellicens',exact:true}).click();
    await page.getByLabel('Produkt (obligatoriskt)',{exact:true}).fill('PRIVATE-DRAFT');
    await page.getByLabel('Licensnyckel och hemliga anteckningar',{exact:true}).selectOption('1');
    await page.getByLabel('Ny licensnyckel',{exact:true}).fill('SYNTHETIC-SECRET-DRAFT');
    const historyState=await page.evaluate(()=>history.state);
    expect(Object.keys(historyState)).toEqual(['valvraEntry']);
    expect(JSON.stringify(historyState)).not.toContain('DRAFT'); expect(page.url()).not.toContain('DRAFT');
    await page.goBack(); await page.goForward(); await focused(page,'Databasplattform');
    await expect(page.getByRole('dialog')).toBeHidden(); await expect(page.locator('#dialog-content')).toBeEmpty();
});
test('missing and inaccessible links reveal no target metadata or protected API calls',async({page})=>{
    const hidden='99999999-9999-9999-9999-999999999999'; const requested=[];
    await page.route('**/api/session',async route=>{
        const response=await route.fetch(); await route.fulfill({response,json:{...await response.json(),isAuditor:false,isAccessAdministrator:false,isSystemAdministrator:false}});
    });
    page.on('request',r=>requested.push(new URL(r.url()).pathname));
    for(const path of ['/resources/'+hidden,'/groups/'+hidden,'/audit','/settings']) {
        await page.goto(path); await visible(page,'Vyn är inte tillgänglig');
        await expect(page.locator('#content')).toBeEmpty(); await audit(page);
    }
    expect(requested.some(x=>x.includes(hidden))).toBe(true); // shell URLs themselves
    expect(requested.some(x=>x.startsWith('/api/')&&(x.includes(hidden)||x.startsWith('/api/audit')||x.startsWith('/api/settings')))).toBe(false);
});
test('late license and audit responses cannot append into a different route',async({page})=>{
    await data(page); await page.goto('/resources');
    let release; const pending=new Promise(r=>release=r);
    await page.route('**/api/resources/*/licenses',async route=>{const response=await route.fetch(); await pending; await route.fulfill({response});});
    const requested=page.waitForRequest(r=>r.url().endsWith('/licenses')&&r.url().includes('/api/'));
    await page.getByRole('link',{name:'Licensöversikt',exact:true}).click(); await requested;
    await page.goBack(); await focused(page,'Dina resurser'); release(); await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading',{name:'Programlicenser',exact:true})).toHaveCount(0);
    await page.unroute('**/api/resources/*/licenses');
    let releaseAudit; const auditPending=new Promise(r=>releaseAudit=r);
    await page.route('**/api/audit/targets',async route=>{const response=await route.fetch(); await auditPending; await route.fulfill({response});});
    const auditRequested=page.waitForRequest(r=>r.url().endsWith('/api/audit/targets'));
    await page.getByRole('link',{name:'Auditlogg',exact:true}).click(); await auditRequested;
    await page.goBack(); await focused(page,'Dina resurser');
    const response=page.waitForResponse(r=>r.url().endsWith('/api/audit/targets')); releaseAudit(); await response; await page.waitForLoadState('networkidle');
    await expect(page.getByLabel('Aktörens ID',{exact:true})).toHaveCount(0);
});
test('links support native new-tab navigation and retain accessible controls',async({page,context})=>{
    const {group}=await data(page); await page.goto('/groups');
    const link=page.getByRole('link',{name:'Öppna grupp · IT och infrastruktur',exact:true});
    await expect(link).toHaveAttribute('href','/groups/'+group.id);
    const newTab=context.waitForEvent('page'); await link.click({modifiers:['Control']});
    const colleague=await newTab; await colleague.waitForLoadState(); await visible(colleague,'IT och infrastruktur');
    await expect(page).toHaveURL('/groups'); await colleague.close();
    await link.focus(); await page.keyboard.press('Enter'); await focused(page,'IT och infrastruktur');
    await page.setViewportSize({width:320,height:800});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true); await audit(page);
});
test('synthetic demo supports reloadable routes and cannot open audit via a direct link',async({page})=>{
    const origin='http://127.0.0.1:58903'; const path='/groups/10000000-0000-0000-0000-000000000001';
    await page.goto(origin+path); await visible(page,'Exempelinfrastruktur');
    await page.getByRole('link',{name:'Öppna resurs · Exempeldatabasserver',exact:true}).click();
    await page.reload(); await visible(page,'Exempeldatabasserver'); await page.goBack(); await focused(page,'Exempelinfrastruktur');
    await page.goto(origin+'/audit'); await visible(page,'Vyn är inte tillgänglig');
});
