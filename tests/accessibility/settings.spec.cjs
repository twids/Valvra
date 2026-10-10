const {test, expect} = require('@playwright/test');
const AxeBuilder = require('@axe-core/playwright').default;
async function audit(page) {
    const result = await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(x => ({id:x.id,nodes:x.nodes.map(n=>n.target)}))).toEqual([]);
}
async function open(page) {
    await page.goto('/');
    await page.getByRole('link',{name:'Inställningar',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Inställningar',level:1})).toBeFocused();
    await expect(page.getByRole('heading',{name:'Globala rättigheter',level:2})).toBeVisible();
}
test('settings support keyboard navigation, directory testing and individual-only role dialogs',async({page})=>{
    await open(page); await expect(page).toHaveURL(/\/settings$/); await audit(page);
    await page.getByRole('button',{name:'Testa anslutning',exact:true}).click();
    await expect(page.getByText('Example person',{exact:true})).toBeVisible(); await audit(page);
    await page.getByRole('button',{name:'+ Tilldela personkonto',exact:true}).click();
    await expect(page.getByRole('dialog',{name:'Tilldela globala rättigheter'})).toBeVisible();
    await expect(page.getByRole('combobox',{name:'Typ'})).toHaveCount(0);
    await page.getByLabel('Sök personkonto',{exact:true}).fill('outsider');
    await page.getByRole('button',{name:'Sök',exact:true}).click();
    await page.getByRole('button',{name:'outsider',exact:true}).click(); await audit(page);
    for(let i=0;i<12;i++) {await page.keyboard.press('Tab'); expect(await page.evaluate(()=>document.querySelector('#dialog').contains(document.activeElement))).toBe(true);}
    await page.keyboard.press('Escape'); await expect(page.getByRole('button',{name:'+ Tilldela personkonto',exact:true})).toBeFocused();
});
test('settings draft survives language changes and failed saves, with localized validation',async({page})=>{
    await open(page);
    await page.getByLabel('Sökbas för personkonton (valfritt)',{exact:true}).fill('OU=Draft,DC=example,DC=test');
    await page.getByLabel('Språk',{exact:true}).selectOption('en');
    await expect(page.getByLabel('User search base (optional)',{exact:true})).toHaveValue('OU=Draft,DC=example,DC=test');
    await expect(page.getByRole('heading',{name:'Global permissions',level:2})).toBeVisible(); await audit(page);
    await page.route('**/api/settings/directory',route=>route.request().method()==='POST' ? route.fulfill({status:409,json:{error:'Reload saved settings.'}}) : route.continue());
    await page.getByRole('button',{name:'Save settings',exact:true}).click();
    await expect(page.getByRole('alert').filter({hasText:'Reload saved settings.'})).toBeVisible();
    await expect(page.getByLabel('User search base (optional)',{exact:true})).toHaveValue('OU=Draft,DC=example,DC=test');
    await page.getByLabel('Directory lookup base (required)',{exact:true}).fill('');
    await page.getByRole('button',{name:'Save settings',exact:true}).click();
    await expect(page.getByLabel('Directory lookup base (required)',{exact:true})).toBeFocused();
    await expect(page.getByRole('alert').filter({hasText:'Complete the required field.'})).toBeVisible();
});
test('ordinary users cannot navigate to settings or call administrative APIs',async({page})=>{
    await page.setExtraHTTPHeaders({'X-Test-Identity':'outsider'});
    await page.goto('/settings');
    await expect(page.getByRole('heading',{name:'Vyn är inte tillgänglig',level:1})).toBeVisible();
    await expect(page.getByRole('link',{name:'Inställningar',exact:true})).toHaveCount(0);
    expect((await page.request.get('/api/settings/directory',{headers:{'X-Test-Identity':'outsider'}})).status()).toBe(403);
});
test('settings remain usable at 320px and with enlarged text',async({page})=>{
    await page.setViewportSize({width:320,height:800}); await open(page); await audit(page);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    await page.route('**/css/site.css',async route=>{const original=await route.fetch(); await route.fulfill({response:original,body:await original.text()+'\nhtml{font-size:200%}'});});
    await page.reload(); await expect(page.getByRole('heading',{name:'Globala rättigheter',level:2})).toBeVisible();
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    await expect(page.getByRole('button',{name:'Spara inställningar',exact:true})).toBeVisible();
});
