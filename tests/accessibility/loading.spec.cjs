const {test,expect}=require('@playwright/test');
test.use({locale:'en-GB'});

test('startup failure has an explicit retry that refreshes identity and metadata',async({page})=>{
    let fail=true,identities=0;page.on('request',r=>{if(r.url().endsWith('/api/session'))identities++;});
    await page.route('**/api/groups',async route=>fail?route.fulfill({status:503,json:{error:'Synthetic unavailable'}}):route.continue());
    await page.goto('/');await expect(page.getByRole('heading',{name:'Could not load this view',exact:true})).toBeVisible();
    fail=false;await page.getByRole('button',{name:'Retry',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Your resources',exact:true})).toBeVisible();expect(identities).toBe(2);
});

test('resource reads expose progress and retry without replaying mutations or secrets',async({page})=>{
    let release,reads=0,reveals=0;const held=new Promise(resolve=>release=resolve);
    page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
    await page.route('**/api/resources/*/secrets',async route=>{reads++;if(reads===1){await held;return route.fulfill({status:503,json:{error:'Synthetic read failure'}});}return route.continue();});
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await expect(page.getByRole('status').filter({hasText:'Loading…'})).toBeVisible();release();
    await expect(page.getByText('Synthetic read failure',{exact:true})).toBeVisible();await page.getByRole('button',{name:'Retry',exact:true}).click();
    await expect(page.getByRole('button',{name:'Show · Administratörskonto',exact:true})).toBeVisible();expect(reveals).toBe(0);
    await expect(page.locator('#content')).not.toHaveAttribute('aria-busy','true');
});

test('late failed resource reads cannot add retry controls to the next view',async({page})=>{
    let release;const held=new Promise(resolve=>release=resolve);
    await page.route('**/api/resources/*/secrets',async route=>{await held;await route.fulfill({status:503,json:{error:'Stale failure'}});});
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await page.getByRole('navigation',{name:'Main navigation'}).getByRole('link',{name:'Resource groups',exact:true}).click();release();
    await expect(page.getByRole('heading',{name:'Resource groups',level:1,exact:true})).toBeVisible();
    await expect(page.getByText('Stale failure',{exact:true})).toHaveCount(0);await expect(page.getByRole('button',{name:'Retry',exact:true})).toHaveCount(0);
});

test('failed audit updates preserve the displayed query and explicit retry',async({page})=>{
    let fail=false;
    await page.route('**/api/audit?*',async route=>fail?route.fulfill({status:503,json:{error:'Synthetic audit failure'}}):route.continue());
    await page.goto('/audit');await expect(page.getByRole('table')).toBeVisible();
    await page.getByText('Advanced filters',{exact:true}).click();
    await page.getByLabel('Operation',{exact:true}).fill('Secret.Create');fail=true;await page.getByRole('button',{name:'Filter',exact:true}).click();
    await expect(page.getByText('Previous results are shown. The update failed.',{exact:true})).toBeVisible();await expect(page.getByRole('table')).toBeVisible();
    fail=false;await page.getByRole('button',{name:'Retry',exact:true}).click();await expect(page.getByRole('status').filter({hasText:'events shown'})).toContainText('6 ');
});

test('unavailable history destinations clear busy state from a previous pending view',async({page})=>{
    let release;const held=new Promise(resolve=>release=resolve);
    await page.route('**/api/resources/*/secrets',async route=>{await held;await route.fulfill({json:[]});});
    await page.goto('/resources/missing');await page.getByRole('link',{name:'← All resources',exact:true}).click();
    await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await expect(page.locator('#content')).toHaveAttribute('aria-busy','true');await page.goBack();await page.goBack();
    await expect(page.getByRole('heading',{name:'This view is unavailable',exact:true})).toBeVisible();
    await expect(page.locator('#content')).not.toHaveAttribute('aria-busy','true');release();
    await expect(page.getByRole('heading',{name:'This view is unavailable',exact:true})).toBeVisible();
});
