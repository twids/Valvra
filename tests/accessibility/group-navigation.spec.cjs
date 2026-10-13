const {test,expect} = require('@playwright/test');
const {clickAction} = require('./ui.cjs');
const AxeBuilder = require('@axe-core/playwright').default;
async function fixture(page,{manage=false}={}) {
    await expect.poll(async () => (await (await page.request.get('/api/resources')).json()).length).toBe(3);
    const originalGroups=await (await page.request.get('/api/groups')).json();
    const originalResources=await (await page.request.get('/api/resources')).json();
    const root=originalGroups.find(x=>x.name==='IT och infrastruktur');
    const groups=[{...root,canManage:manage},{id:'child',parentId:root.id,name:'Servrar',canManage:manage},
        {id:'deep',parentId:'child',name:'Produktion',canManage:manage},{id:'other',parentId:null,name:'Övrigt',canManage:false},
        {id:'orphan',parentId:'not-visible',name:'Synlig egen grupp',canManage:false}];
    const resources=originalResources.map(x=>({...x,groupId:x.name==='Windows-servrar'?'child':x.name==='Nätverk och åtkomst'?'deep':root.id}));
    resources.push({id:'unrelated',groupId:'other',name:'Separat resurs',permissions:1,canManage:false});
    await page.route('**/api/groups',route=>route.fulfill({json:groups}));
    await page.route('**/api/resources',route=>route.fulfill({json:resources}));
    const calls=[]; page.on('request',request=>{if(request.url().includes('/api/')) calls.push({url:request.url(),method:request.method()});});
    await page.goto('/'); await page.getByRole('navigation',{name:'Huvudmeny'}).getByRole('link',{name:'Resursgrupper',exact:true}).click();
    return {root,calls};
}
async function openRoot(page) { await page.getByRole('link',{name:'Öppna grupp · IT och infrastruktur',exact:true}).click(); }
async function audit(page) {
    const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(x=>({id:x.id,nodes:x.nodes.map(n=>n.target)}))).toEqual([]);
}

test('group overview shows a navigable nested list using only available group metadata',async({page})=>{
    const {calls}=await fixture(page);
    const rootItem=page.locator('li').filter({has:page.getByRole('link',{name:'Öppna grupp · IT och infrastruktur',exact:true})}).first();
    await expect(rootItem.getByRole('link',{name:'Öppna grupp · Produktion',exact:true})).toBeVisible();
    await expect(rootItem).toContainText('Synliga resurser: 3');
    await expect(page.getByRole('link',{name:'Öppna grupp · Synlig egen grupp',exact:true})).toBeVisible();
    expect((await page.locator('#content').innerText())).not.toContain('not-visible');
    await audit(page); await openRoot(page);
    await expect(page.getByRole('heading',{name:'IT och infrastruktur',level:1,exact:true})).toBeFocused();
    await expect(page.getByRole('navigation',{name:'Huvudmeny'}).getByRole('link',{name:'Resursgrupper',exact:true})).toHaveAttribute('aria-current','page');
    await expect(page.getByRole('status').filter({hasText:'Synliga resurser'})).toHaveText('Synliga resurser: 3');
    await expect(page.getByRole('heading',{name:'Separat resurs'})).toHaveCount(0);
    await expect(page.getByRole('button',{name:'Behörigheter',exact:true})).toHaveCount(0);
    expect(calls.some(x=>x.url.includes('/reveal') || /\/resources\/.+\/(secrets|licenses)/.test(x.url))).toBe(false);
    await audit(page);
});

test('group branches collapse and expand without fetching additional metadata',async({page})=>{
    const {calls}=await fixture(page);
    const before=calls.length;
    await page.getByRole('button',{name:'Dölj alla undergrupper',exact:true}).click();
    await expect(page.getByRole('link',{name:'Öppna grupp · Produktion',exact:true})).toBeHidden();
    await page.getByRole('button',{name:'Visa alla undergrupper',exact:true}).click();
    await expect(page.getByRole('link',{name:'Öppna grupp · Produktion',exact:true})).toBeVisible();
    await page.getByRole('button',{name:'Undergrupper · IT och infrastruktur',exact:true}).click();
    await expect(page.getByRole('link',{name:'Öppna grupp · Servrar',exact:true})).toBeHidden();
    expect(calls.length).toBe(before);
});

test('group resources include grandchildren, direct-only selection and search remain scoped',async({page})=>{
    await fixture(page); await openRoot(page);
    await expect(page.getByRole('heading',{name:'Nätverk och åtkomst',level:3,exact:true})).toBeVisible();
    await page.getByLabel('Inkludera undergrupper',{exact:true}).uncheck();
    await expect(page.getByRole('status').filter({hasText:'Synliga resurser'})).toHaveText('Synliga resurser: 1');
    await expect(page.getByRole('heading',{name:'Windows-servrar',level:3,exact:true})).toHaveCount(0);
    await page.getByLabel('Inkludera undergrupper',{exact:true}).check();
    await page.getByRole('textbox',{name:'Sök resurser',exact:true}).fill('Nätverk');
    await expect(page.getByRole('status').filter({hasText:'Synliga resurser'})).toHaveText('Synliga resurser: 1');
    await expect(page.getByRole('heading',{name:'Nätverk och åtkomst',level:3,exact:true})).toBeVisible();
    await page.getByRole('link',{name:'Öppna resurs · Nätverk och åtkomst',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Nätverk och åtkomst',level:1,exact:true})).toBeFocused();
    await expect(page.getByRole('navigation',{name:'Gruppsökväg'})).toContainText('Produktion');
    await page.getByRole('link',{name:'← Till resursgruppen',exact:true}).click();
    await expect(page.getByRole('heading',{name:'IT och infrastruktur',level:1,exact:true})).toBeFocused();
    await expect(page.getByRole('textbox',{name:'Sök resurser',exact:true})).toHaveValue('Nätverk');
    await page.getByLabel('Språk',{exact:true}).selectOption('en');
    await expect(page.getByRole('textbox',{name:'Search resources',exact:true})).toHaveValue('Nätverk');
    await expect(page.getByRole('checkbox',{name:'Include subgroups',exact:true})).toBeChecked();
    await expect(page.getByRole('status').filter({hasText:'Visible resources'})).toHaveText('Visible resources: 1');
    await audit(page);
});

test('subgroup and breadcrumb navigation keep resource context and support keyboard activation',async({page})=>{
    await fixture(page); await openRoot(page);
    const child=page.getByRole('link',{name:'Öppna grupp · Servrar',exact:true});
    await child.focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading',{name:'Servrar',level:1,exact:true})).toBeFocused();
    await expect(page.getByRole('status').filter({hasText:'Synliga resurser'})).toHaveText('Synliga resurser: 2');
    await page.getByRole('link',{name:'Öppna grupp · Produktion',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Produktion',level:1,exact:true})).toBeFocused();
    await page.getByRole('navigation',{name:'Gruppsökväg'}).getByRole('link',{name:'IT och infrastruktur',exact:true}).click();
    await expect(page.getByRole('heading',{name:'IT och infrastruktur',level:1,exact:true})).toBeFocused();
    await page.getByRole('link',{name:'← Alla resursgrupper',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Resursgrupper',level:1,exact:true})).toBeFocused();
});

test('resource overview group filter includes descendants and survives resource return and refresh',async({page})=>{
    await fixture(page);
    await page.getByRole('navigation',{name:'Huvudmeny'}).getByRole('link',{name:'Resurser',exact:true}).click();
    await page.getByLabel('Resursgrupp',{exact:true}).selectOption('child');
    await expect(page.getByRole('heading',{name:'Windows-servrar',level:2,exact:true})).toBeVisible();
    await expect(page.getByRole('heading',{name:'Nätverk och åtkomst',level:2,exact:true})).toBeVisible();
    await page.getByLabel('Inkludera undergrupper',{exact:true}).uncheck();
    await page.getByRole('link',{name:'Öppna resurs · Windows-servrar',exact:true}).click();
    await page.getByRole('link',{name:'← Alla resurser',exact:true}).click();
    await expect(page.getByLabel('Resursgrupp',{exact:true})).toHaveValue('child');
    await expect(page.getByLabel('Inkludera undergrupper',{exact:true})).not.toBeChecked();
    await page.getByRole('button',{name:'Uppdatera',exact:true}).click();
    await expect(page.getByLabel('Resursgrupp',{exact:true})).toHaveValue('child');
    await expect(page.getByRole('heading',{name:'Nätverk och åtkomst',level:2,exact:true})).toHaveCount(0);
});

test('creation dialogs select the group being viewed without reading saved secrets',async({page})=>{
    const {root}=await fixture(page,{manage:true}); await openRoot(page);
    await page.getByRole('button',{name:'+ Ny resurs',exact:true}).click();
    await expect(page.getByRole('dialog').getByLabel('Resursgrupp',{exact:true})).toHaveValue(root.id);
    await page.keyboard.press('Escape'); await page.getByRole('button',{name:'+ Ny undergrupp',exact:true}).click();
    await expect(page.getByRole('dialog').getByLabel('Överordnad grupp',{exact:true})).toHaveValue(root.id);
    await page.keyboard.press('Escape');
    const management=page.locator('#page-actions .group-management>button');
    await management.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('#page-actions').getByRole('menuitem',{name:'Byt namn · IT och infrastruktur',exact:true})).toBeVisible();
    await audit(page);
    await page.keyboard.press('Escape');
    await expect(management).toBeFocused();
    await expect(page.locator('#page-actions').getByRole('menuitem',{name:'Byt namn · IT och infrastruktur',exact:true})).toBeHidden();
    await page.setViewportSize({width:320,height:800});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true); await audit(page);
});

test('group action menu does not resize rows and supports keyboard and outside dismissal',async({page})=>{
    await fixture(page,{manage:true});
    const trigger=page.getByRole('button',{name:'Hantera grupp · IT och infrastruktur',exact:true});
    const row=trigger.locator('..').locator('..');
    const before=await row.boundingBox();
    await trigger.click();
    const after=await row.boundingBox(); expect(after.height).toBeCloseTo(before.height,0);
    await expect(page.getByRole('menuitem',{name:'Behörigheter · IT och infrastruktur',exact:true})).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('menuitem',{name:'Byt namn · IT och infrastruktur',exact:true})).toBeFocused();
    await page.keyboard.press('Escape'); await expect(trigger).toBeFocused();
    await trigger.click(); await expect(page.getByRole('menuitem',{name:'Behörigheter · IT och infrastruktur',exact:true})).toBeVisible();
    await page.keyboard.press('Tab'); await expect(page.getByRole('button',{name:'Undergrupper · Servrar',exact:true})).toBeFocused();
    await trigger.click(); await page.keyboard.press('Shift+Tab'); await expect(page.getByRole('link',{name:'Öppna grupp · IT och infrastruktur',exact:true})).toBeFocused();
    await page.getByRole('button',{name:'Hantera grupp · Servrar',exact:true}).click();
    await trigger.click(); await expect(page.getByRole('menuitem',{name:'Behörigheter · IT och infrastruktur',exact:true})).toBeFocused();
    await page.getByRole('heading',{name:'Resursgrupper',level:1}).click();
    await expect(trigger).toHaveAttribute('aria-expanded','false');
});

test('move previews show full destinations and added removed retained access before any move',async({page})=>{
    await page.goto('/');
    const session=await (await page.request.get('/api/session')).json();
    const headers={'X-CSRF-TOKEN':session.csrfToken};
    const created=await page.request.post('/api/groups',{headers,data:{name:'Move destination',parentId:null}});
    expect(created.ok()).toBe(true); const destination=await created.json();
    const grantSession=await (await page.request.get('/api/session')).json();
    const granted=await page.request.post('/api/grants',{headers:{'X-CSRF-TOKEN':grantSession.csrfToken},data:{targetKind:0,targetId:destination.id,subjectKind:0,provider:'ad',subjectId:'outsider',permissions:3}});
    expect(granted.ok(),await granted.text()).toBe(true);
    let moves=0;
    await page.route('**/api/move/1/*',route=>{moves++;return route.fulfill({status:204});});
    await page.reload();
    await page.getByRole('link',{name:'Öppna resurs · Databasplattform',exact:true}).click();
    await clickAction(page,'Flytta till annan grupp…');
    const picker=page.getByLabel('Ny resursgrupp',{exact:true});
    await expect(picker).toHaveValue(''); await expect(picker).toContainText('Move destination · '+destination.id);
    await picker.selectOption(destination.id);
    await page.getByRole('button',{name:'Förhandsgranska åtkomst',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Tillkommer',exact:true})).toBeVisible();
    await expect(page.getByRole('heading',{name:'Försvinner',exact:true})).toBeVisible();
    await expect(page.getByRole('heading',{name:'Behålls',exact:true})).toBeVisible(); expect(moves).toBe(0);
    await page.getByRole('button',{name:'← Tillbaka',exact:true}).click();
    await expect(picker).toHaveValue(destination.id); expect(moves).toBe(0);
    await page.keyboard.press('Escape');
});

test('late resource metadata cannot replace the group view after navigating back',async({page})=>{
    await fixture(page); await openRoot(page);
    let release; const pending=new Promise(resolve=>release=resolve);
    await page.route('**/api/resources/*/secrets',async route=>{
        const response=await route.fetch(); await pending; await route.fulfill({response});
    });
    const requested=page.waitForRequest(request=>/\/api\/resources\/[^/]+\/secrets$/.test(request.url()));
    await page.getByRole('link',{name:'Öppna resurs · Databasplattform',exact:true}).click(); await requested;
    await expect(page.getByRole('heading',{name:'Databasplattform',level:1,exact:true})).toBeVisible();
    await page.getByRole('link',{name:'← Till resursgruppen',exact:true}).click();
    const response=page.waitForResponse(response=>/\/api\/resources\/[^/]+\/secrets$/.test(response.url()));
    release(); await response;
    await expect(page.getByRole('heading',{name:'IT och infrastruktur',level:1,exact:true})).toBeFocused();
    await expect(page.getByRole('heading',{name:'Lösenord',level:2,exact:true})).toHaveCount(0);
    await expect(page.getByRole('status').filter({hasText:'Synliga resurser'})).toHaveText('Synliga resurser: 3');
});

test('group hierarchy and group content retain WCAG controls and reflow at 320px',async({page})=>{
    await fixture(page); await page.setViewportSize({width:320,height:800});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true); await audit(page);
    await openRoot(page);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true); await audit(page);
    await page.getByRole('link',{name:'Öppna grupp · Servrar',exact:true}).click();
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true); await audit(page);
});
