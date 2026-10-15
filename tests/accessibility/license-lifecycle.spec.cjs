const {test,expect}=require('@playwright/test');
const {clickAction}=require('./ui.cjs');
test.use({locale:'en-GB'});
test('license creation separates required secrets from optional commercial data',async({page})=>{
    await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await page.getByRole('button',{name:'+ Add license',exact:true}).click();
    const dialog=page.getByRole('dialog');
    await expect(dialog.getByRole('combobox')).toHaveCount(0);
    await expect(dialog.getByLabel('License key (required)',{exact:true})).toBeVisible();
    await expect(dialog.getByLabel('Vendor',{exact:true})).not.toBeVisible();
    await dialog.getByText('Vendor, purchase and validity (optional)',{exact:true}).click();
    await expect(dialog.getByLabel('Vendor',{exact:true})).toBeVisible();
    await expect(dialog.getByRole('button',{name:'Add license',exact:true})).toBeVisible();
});
test('license deletion identifies the resource and preserves the recovery message',async({page})=>{
    await page.goto('/licenses');
    const row=page.getByRole('row').filter({hasText:'Exempellicens'}).first();
    await expect(row.getByRole('link')).toBeVisible();const path=await row.getByRole('link').textContent();await clickAction(row,/^Delete/);
    const dialog=page.getByRole('dialog');await expect(dialog).toContainText(path);
    await expect(dialog).toContainText('Assigned seats:');await expect(dialog.getByRole('link',{name:'Open resource',exact:true})).toBeVisible();
    await page.route('**/api/licenses/*/deleted',route=>route.fulfill({json:{}}));
    await dialog.getByRole('button',{name:'Delete',exact:true}).click();
    await expect(page.getByText('The license is in the resource recycle bin.',{exact:true})).toBeVisible();
});
