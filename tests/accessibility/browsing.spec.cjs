const {test,expect}=require('@playwright/test');
const AxeBuilder=require('@axe-core/playwright').default;
const {clickAction}=require('./ui.cjs');
test.use({locale:'en-GB'});

test('compact browsing sorts and clears filters without reading secret contents',async({page})=>{
    let reads=0;
    page.on('request',request=>{if(/\/api\/resources\/[^/]+\/(secrets|licenses)$/.test(request.url()))reads++;});
    await page.route('**/api/resources',async route=>{
        const response=await route.fetch(), original=await response.json();
        await route.fulfill({response,json:Array.from({length:150},(_,i)=>({...original[0],id:'resource-'+i,name:'Resource '+String(i).padStart(3,'0'),permissions:1,canManage:false}))});
    });
    await page.goto('/');
    await page.getByLabel('View',{exact:true}).selectOption('list');
    await page.getByLabel('Sort',{exact:true}).selectOption('reverse');
    await expect(page.getByRole('row').nth(1)).toContainText('Resource 149');
    await expect(page.getByRole('status')).toContainText('150 resources shown');
    await page.getByRole('textbox',{name:'Search resources',exact:true}).fill('149');
    await expect(page.getByRole('row')).toHaveCount(2);
    await page.getByRole('button',{name:'Clear filters',exact:true}).click();
    await expect(page.getByRole('row')).toHaveCount(151); expect(reads).toBe(0);
});

test('layout and sorting survive resource navigation and browser back',async({page})=>{
    await page.goto('/'); await page.getByLabel('View',{exact:true}).selectOption('list');
    await page.getByLabel('Sort',{exact:true}).selectOption('reverse');
    await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await page.goBack();
    await expect(page.getByLabel('View',{exact:true})).toHaveValue('list');
    await expect(page.getByLabel('Sort',{exact:true})).toHaveValue('reverse');
});

test('mobile resource records keep show copy and secondary actions reachable without table scrolling',async({page})=>{
    await page.setViewportSize({width:320,height:800}); await page.goto('/');
    await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await expect(page.getByRole('button',{name:'Show · Administratörskonto',exact:true})).toBeVisible();
    await expect(page.getByRole('button',{name:'Copy · Administratörskonto',exact:true})).toBeVisible();
    for(const wrapper of await page.locator('.table-wrap:has(.mobile-records)').all())
        expect(await wrapper.evaluate(x=>x.scrollWidth<=x.clientWidth+1)).toBe(true);
    await clickAction(page,'Modify · Administratörskonto');
    await expect(page.getByRole('dialog')).toBeVisible(); await page.keyboard.press('Escape');
    await expect(page.getByRole('button',{name:'More actions · Administratörskonto',exact:true})).toBeFocused();
    const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(x=>({id:x.id,nodes:x.nodes.map(n=>n.target)}))).toEqual([]);
});
