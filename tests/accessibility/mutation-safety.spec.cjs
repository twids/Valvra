const {test,expect}=require('@playwright/test');const {clickAction}=require('./ui.cjs');test.use({locale:'en-GB'});
async function edit(page){await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();await clickAction(page,'Modify · Administratörskonto');}
test('password metadata edit never reveals and blur cannot silently replace secret fields',async({page})=>{
 let reveals=0,posted;page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
 await page.route('**/api/secrets/*/update',route=>{posted=route.request().postDataJSON();return route.fulfill({status:204});});await edit(page);
 await page.getByLabel('Password and secret fields',{exact:true}).selectOption('1');await page.getByLabel('Username',{exact:true}).fill('draft-user');await page.getByLabel('Password (required)',{exact:true}).fill('draft-password');await page.getByLabel('Secret notes',{exact:true}).fill('draft-notes');
 await page.evaluate(()=>window.dispatchEvent(new Event('blur')));await expect(page.getByLabel('Password and secret fields',{exact:true})).toHaveValue('0');
 await expect(page.getByLabel('Password',{exact:true})).toHaveValue('');await expect(page.getByLabel('Username',{exact:true})).toHaveValue('');
 await page.getByRole('button',{name:'Save',exact:true}).click();await expect.poll(()=>posted).toMatchObject({secretChange:0,payload:null});expect(reveals).toBe(0);
});
test('successful save after blur retires form and shows acknowledgement exactly once',async({page})=>{
 let release,writes=0;const pending=new Promise(resolve=>release=resolve);await page.route('**/api/secrets/*/update',async route=>{writes++;await pending;await route.fulfill({status:204});});await edit(page);
 await page.getByRole('button',{name:'Save',exact:true}).click();await expect.poll(()=>writes).toBe(1);await page.evaluate(()=>window.dispatchEvent(new Event('blur')));release();
 await expect(page.getByRole('dialog')).not.toBeVisible();await expect(page.locator('#notice')).toContainText('Completed and audited');expect(writes).toBe(1);
});
test('completion of dismissed form does not close a newer dialog',async({page})=>{
 let release,writes=0;const pending=new Promise(resolve=>release=resolve);await page.route('**/api/secrets/*/update',async route=>{writes++;await pending;await route.fulfill({status:204});});await edit(page);
 await page.getByRole('button',{name:'Save',exact:true}).click();await expect.poll(()=>writes).toBe(1);await page.keyboard.press('Escape');await page.getByRole('button',{name:'Permissions',exact:true}).click();await expect(page.getByRole('dialog')).toContainText('Permissions');release();
 await expect(page.locator('#notice')).toContainText('Completed and audited');await expect(page.getByRole('dialog')).toContainText('Permissions');
});
