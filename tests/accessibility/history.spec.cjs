const {test,expect}=require('@playwright/test');
const {clickAction}=require('./ui.cjs');
test.use({locale:'en-GB'});

async function open(page,kind){
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    const row=page.getByRole('row').filter({hasText:kind==='secret'?'Administratörskonto':'Exempellicens'});
    await clickAction(row,/^History/);await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();
}
for(const kind of ['secret','license']){
    test(kind+' version Back and Escape clear values and return to metadata without repeating reveal',async({page})=>{
        let reveals=0;page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
        await open(page,kind);await page.getByRole('dialog').getByRole('button',{name:'Show · 1',exact:true}).click();
        await expect(page.getByRole('timer')).toHaveText('Fields clear in 30 seconds.');
        const values=page.getByRole('dialog').locator('textarea');expect(await values.count()).toBeGreaterThan(0);
        await page.getByRole('button',{name:'← Back',exact:true}).click();
        await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();await expect(page.getByRole('dialog').locator('textarea')).toHaveCount(0);expect(reveals).toBe(1);
        await page.getByRole('dialog').getByRole('button',{name:'Show · 1',exact:true}).click();await expect(page.getByRole('timer')).toBeVisible();await page.keyboard.press('Escape');
        await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();expect(reveals).toBe(2);
    });
    test(kind+' countdown expires and focus loss closes without reopening history or revealing again',async({page})=>{
        await page.clock.install();let reveals=0;page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
        await open(page,kind);await page.getByRole('dialog').getByRole('button',{name:'Show · 1',exact:true}).click();await expect(page.getByRole('timer')).toBeVisible();
        await page.clock.fastForward(10000);await expect(page.getByRole('timer')).toHaveText('Fields clear in 20 seconds.');
        await page.clock.fastForward(20000);await expect(page.getByRole('dialog')).not.toBeVisible();await expect(page.locator('#dialog-content textarea')).toHaveCount(0);expect(reveals).toBe(1);
        await open(page,kind);await page.getByRole('dialog').getByRole('button',{name:'Show · 1',exact:true}).click();await expect(page.getByRole('timer')).toBeVisible();
        await page.evaluate(()=>window.dispatchEvent(new Event('blur')));await expect(page.getByRole('dialog')).not.toBeVisible();await expect(page.locator('#dialog-content textarea')).toHaveCount(0);expect(reveals).toBe(2);
    });
}

test('history reads show progress and explicit retry with no secret release',async({page})=>{
    let fail=true,release;const held=new Promise(resolve=>release=resolve);let reveals=0;
    page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
    await page.route('**/api/secrets/*/versions',async route=>{if(fail){await held;return route.fulfill({status:503,json:{error:'Synthetic history failure'}});}return route.continue();});
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();await clickAction(page,'History · Administratörskonto');
    await expect(page.getByRole('dialog').getByRole('status')).toHaveText('Loading…');release();await expect(page.getByText('Synthetic history failure',{exact:true})).toBeVisible();
    fail=false;await page.getByRole('button',{name:'Retry',exact:true}).click();await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();expect(reveals).toBe(0);
});

test('focus loss invalidates a pending history read and retry does not reveal content',async({page})=>{
    let release,held=true;const pending=new Promise(resolve=>release=resolve);
    await page.route('**/api/secrets/*/versions',async route=>{if(held)await pending;return route.continue();});
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();await clickAction(page,'History · Administratörskonto');
    await expect(page.getByRole('dialog').getByRole('status')).toBeVisible();await page.evaluate(()=>window.dispatchEvent(new Event('blur')));held=false;release();
    await expect(page.getByText('Reading stopped when the page lost focus. Retry to read current metadata.',{exact:true})).toBeVisible();
    await page.getByRole('button',{name:'Retry',exact:true}).click();await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();await expect(page.getByRole('dialog').locator('textarea')).toHaveCount(0);
});

test('closing pending history clears shared busy state before opening another dialog',async({page})=>{
    let release;const pending=new Promise(resolve=>release=resolve);
    await page.route('**/api/secrets/*/versions',async route=>{await pending;return route.continue();});
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();await clickAction(page,'History · Administratörskonto');
    await expect(page.locator('#dialog-content')).toHaveAttribute('aria-busy','true');await page.keyboard.press('Escape');
    await page.getByRole('button',{name:'Show · Administratörskonto',exact:true}).click();await expect(page.getByRole('timer')).toBeVisible();
    await expect(page.locator('#dialog-content')).not.toHaveAttribute('aria-busy','true');release();
    await expect(page.getByRole('dialog').locator('textarea')).toHaveCount(3);await page.keyboard.press('Escape');
});
