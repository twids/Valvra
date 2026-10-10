const {test, expect} = require('@playwright/test');
const AxeBuilder = require('@axe-core/playwright').default;
test.use({locale: 'en-GB'});

async function audit(page) {
    const result = await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(v=>({id:v.id,nodes:v.nodes.map(n=>n.target)}))).toEqual([]);
}
async function openResource(page) {
    await page.goto('/');
    await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Databasplattform',exact:true})).toBeFocused();
}

test('license overview creates a license in the selected writable resource',async({page})=>{
    await page.route('**/api/resources',async route=>{
        const response=await route.fetch(); const resources=await response.json();
        resources.find(x=>x.name==='Databasplattform').permissions=3;
        await route.fulfill({response,json:resources});
    });
    await page.goto('/');
    await page.getByRole('navigation').getByRole('link',{name:'License overview',exact:true}).click();
    await page.getByRole('button',{name:'+ Add license',exact:true}).click();
    const picker=page.getByLabel('Resource',{exact:true});
    await expect(picker.locator('option')).toHaveCount(2);
    await expect(picker).not.toContainText('Databasplattform');
    await picker.selectOption({label:'Windows-servrar'});
    const target=await picker.inputValue();
    await page.getByLabel('Product (required)',{exact:true}).fill('Overview license');
    await page.getByLabel('New license key',{exact:true}).fill('SYNTHETIC-OVERVIEW-LICENSE');
    await audit(page);
    const request=page.waitForRequest(r=>r.url().endsWith('/api/licenses')&&r.method()==='POST');
    await page.getByRole('button',{name:'Save',exact:true}).click();
    expect((await request).postDataJSON().resourceId).toBe(target);
    await expect(page.getByRole('dialog')).not.toBeVisible();
    await expect(page.getByRole('heading',{name:'License overview',level:1})).toBeVisible();
    await expect(page.getByRole('row').filter({hasText:'Overview license'})).toContainText('Windows-servrar');
});

test('license overview hides creation when no resource is writable',async({page})=>{
    await page.route('**/api/resources',async route=>{
        const response=await route.fetch(); const resources=await response.json();
        resources.forEach(x=>x.permissions=3);
        await route.fulfill({response,json:resources});
    });
    await page.goto('/');
    await page.getByRole('navigation').getByRole('link',{name:'License overview',exact:true}).click();
    await expect(page.getByRole('heading',{name:'License overview',level:1})).toBeVisible();
    await expect(page.getByRole('button',{name:'+ Add license',exact:true})).toHaveCount(0);
});

test('English navigation, document language, headings and accessibility across all views',async({page})=>{
    await page.goto('/');
    await expect(page.getByRole('heading',{name:'Your resources',level:1})).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang','en');
    await audit(page);
    for(const title of ['Resource groups','License overview','Audit log']) {
        await page.getByRole('navigation',{name:'Main navigation'}).getByRole('link',{name:title,exact:true}).click();
        await expect(page.getByRole('heading',{name:title,level:1})).toBeFocused();
        await expect(page).toHaveTitle(title+' · Valvra');
        await audit(page);
    }
});

test('explicit choice survives reload and navigation, preserving search and original resource names',async({page})=>{
    await page.goto('/');
    await page.getByRole('textbox',{name:'Search resources',exact:true}).fill('Databasplattform');
    await page.getByLabel('Language',{exact:true}).selectOption('sv');
    await expect(page.getByRole('heading',{name:'Dina resurser',level:1})).toBeVisible();
    await expect(page.getByRole('textbox',{name:'Sök resurser',exact:true})).toHaveValue('Databasplattform');
    await page.reload();
    await expect(page.getByRole('heading',{name:'Dina resurser',level:1})).toBeVisible();
    await page.getByLabel('Språk',{exact:true}).selectOption('en');
    await expect(page.getByRole('heading',{name:'Your resources',level:1})).toBeVisible();
    await expect(page.getByRole('heading',{name:'Databasplattform',level:2})).toBeVisible();
    const cookie=(await page.context().cookies()).find(c=>c.name==='Valvra.Language');
    expect(cookie.value).toBe('en'); expect(cookie.sameSite).toBe('Lax');
    await page.reload(); await expect(page.getByLabel('Language',{exact:true})).toHaveValue('en');
});

test('user-supplied text matching a translation key is displayed verbatim',async({page})=>{
    await page.route('**/api/resources',async route=>{
        const response=await route.fetch(); const resources=await response.json();
        resources[0].name='Spara'; await route.fulfill({response,json:resources});
    });
    await page.goto('/');
    await expect(page.getByRole('heading',{name:'Spara',level:2,exact:true})).toBeVisible();
    await page.getByRole('link',{name:'Open resource · Spara',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Spara',level:1,exact:true})).toBeVisible();
});

test('English license forms expose localized validation and retain existing focus-loss protection',async({page})=>{
    await openResource(page);
    await page.getByRole('row').filter({hasText:'Exempellicens'}).getByRole('button',{name:/^Modify/}).click();
    await expect(page.getByRole('dialog',{name:'Edit license',exact:true})).toBeVisible();
    const product=page.getByLabel('Product (required)',{exact:true});
    await product.fill(''); await page.getByRole('button',{name:'Save',exact:true}).click();
    await expect(product).toBeFocused();
    await expect(page.getByRole('alert')).toContainText('Complete the required field.');
    await product.fill('English draft');
    await page.getByLabel('License key and secret notes',{exact:true}).selectOption('1');
    await page.getByLabel('New license key',{exact:true}).fill('synthetic-only');
    await page.evaluate(()=>window.dispatchEvent(new Event('blur')));
    await expect(product).toHaveValue('English draft');
    await expect(page.getByLabel('New license key',{exact:true})).toHaveValue('');
    await expect(page.getByRole('button',{name:'Save',exact:true})).toBeDisabled();
    await expect(page.getByRole('alert')).toContainText('Secret fields have been cleared.');
    await audit(page);
});

test('installation language switches in place without losing entered values or focus',async({page})=>{
    await page.route('**/api/setup/status',route=>route.fulfill({json:{canConfigure:true,csrfToken:'synthetic'}}));
    await page.goto('/setup');
    await expect(page.getByRole('heading',{name:'Welcome to Valvra',level:1})).toBeVisible();
    await page.getByLabel('Setup code (required)',{exact:true}).fill('synthetic-code');
    await page.getByLabel('Website DNS name (required)',{exact:true}).fill('synthetic.example');
    await page.getByLabel('Language',{exact:true}).selectOption('sv');
    await expect(page.getByLabel('Installationskod (obligatoriskt)',{exact:true})).toHaveValue('synthetic-code');
    await expect(page.getByLabel('Webbplatsens DNS-namn (obligatoriskt)',{exact:true})).toHaveValue('synthetic.example');
    await page.getByLabel('Språk',{exact:true}).focus();
    await page.getByLabel('Språk',{exact:true}).selectOption('en');
    await expect(page.getByLabel('Language',{exact:true})).toBeFocused();
    await page.getByRole('button',{name:'Next →',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Connect the databases',exact:true})).toBeFocused();
    await page.getByLabel('Use a separate audit reader (optional)',{exact:true}).check();
    await expect(page.locator('#setup-readerServer')).toBeEnabled();
    await audit(page);
});

test('synthetic demo ships English catalogs and English secret dialogs',async({page})=>{
    await page.goto('http://127.0.0.1:58903/');
    await expect(page.getByRole('heading',{name:'Your resources',level:1})).toBeVisible();
    await page.getByRole('link',{name:'Open resource · Exempeldatabasserver',exact:true}).click();
    await page.getByRole('button',{name:'Show key · Exempelprogram',exact:true}).click();
    await expect(page.getByRole('dialog',{name:'License key · Exempelprogram'})).toBeVisible();
    await expect(page.getByLabel('Notes',{exact:true})).toBeVisible();
    await audit(page);
});

test('validated setup results switch language without discarding configuration or successful checks',async({page})=>{
    await page.route('**/api/setup/status',route=>route.fulfill({json:{canConfigure:true,csrfToken:'synthetic'}}));
    let requestLanguage;
    await page.route('**/api/setup/validate',route=>{
        requestLanguage=route.request().headers()['x-valvra-language'];
        return route.fulfill({json:{passed:true,checks:[{name:'Configuration',nameKey:'Konfiguration',passed:true,
            message:'Databases and keys are separate; TLS is required.',messageKey:'Databaser och nycklar är åtskilda; TLS krävs.'}]}});
    });
    await page.goto('/setup'); await page.getByLabel('Setup code (required)',{exact:true}).fill('synthetic-code');
    await page.getByRole('button',{name:'Next →',exact:true}).click();
    for(const field of ['vaultServer','writerServer']) await page.locator('#setup-'+field).fill('synthetic.example.invalid');
    await page.getByRole('button',{name:'Next →',exact:true}).click();
    for(const field of ['adServer','baseDn','encryptionThumbprint','auditThumbprint','integrityThumbprint']) await page.locator('#setup-'+field).fill('synthetic');
    await page.getByRole('button',{name:'Next →',exact:true}).click();
    await page.getByRole('button',{name:'Test connections and permissions',exact:true}).click();
    await expect(page.locator('.setup-check strong')).toHaveText('Passed: Configuration'); expect(requestLanguage).toBe('en');
    await page.getByLabel('Language',{exact:true}).selectOption('sv');
    await expect(page.locator('.setup-check strong')).toHaveText('Godkänd: Konfiguration');
    await expect(page.locator('.setup-check p')).toHaveText('Databaser och nycklar är åtskilda; TLS krävs.');
    await expect(page.getByRole('button',{name:'Spara och slutför installationen',exact:true})).toBeEnabled();
    await page.getByLabel('Språk',{exact:true}).selectOption('en');
    await expect(page.locator('.setup-check strong')).toHaveText('Passed: Configuration');
    await expect(page.locator('#setup-vaultServer')).toHaveValue('synthetic.example.invalid');
    await audit(page);
});

test('English layout remains accessible at 320px and with 200% text',async({page})=>{
    await page.setViewportSize({width:320,height:800}); await page.goto('/');
    await expect(page.getByRole('heading',{name:'Your resources',level:1})).toBeVisible();
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    await audit(page);
    await page.route('**/css/site.css',async route=>{
        const original=await route.fetch();
        await route.fulfill({response:original,body:await original.text()+'\nhtml{font-size:200%}'});
    });
    await page.reload(); await expect(page.getByRole('heading',{name:'Your resources',level:1})).toBeVisible();
    expect(await page.locator('body').evaluate(el=>getComputedStyle(el).fontSize)).toBe('32px');
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    await audit(page);
});
