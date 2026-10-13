const {test,expect}=require('@playwright/test');const {clickAction}=require('./ui.cjs');test.use({locale:'en-GB'});
async function open(page){await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();await clickAction(page,'Assignments · Exempellicens');await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();}
test('assignment destination is explicit and Add/Cancel return to overview',async({page})=>{
 await open(page);await page.getByRole('button',{name:'+ Assign seats',exact:true}).click();await page.getByLabel('Assign to',{exact:true}).selectOption('resource');
 const target=page.getByLabel('Resource',{exact:true});await expect(target).toHaveValue('');await expect(target.locator('option').filter({hasText:'Databasplattform'})).toContainText('IT och infrastruktur / Databasplattform · ');
 await page.getByRole('button',{name:'← Back',exact:true}).click();await expect(page.getByRole('dialog').getByRole('table')).toBeVisible();
 await page.getByRole('button',{name:'+ Assign seats',exact:true}).click();await page.getByLabel('Search Active Directory',{exact:true}).fill('x');await page.getByLabel('Assign to',{exact:true}).selectOption('resource');
 const picker=page.getByLabel('Resource',{exact:true});await picker.selectOption(await picker.locator('option').filter({hasText:'Databasplattform'}).getAttribute('value'));await page.getByRole('button',{name:'Assign seats',exact:true}).click();
 await expect(page.getByRole('dialog')).toContainText('1 of 20 seats assigned.');await expect(page.getByRole('dialog').getByRole('table')).toContainText('IT och infrastruktur / Databasplattform');
 const reclaim=page.getByRole('dialog').getByRole('button',{name:/^Reclaim seats ·/});await reclaim.click();await expect(page.getByRole('dialog')).toContainText('Reclaim 1 seats from IT och infrastruktur / Databasplattform?');
 await page.getByRole('button',{name:'← Back',exact:true}).click();await expect(page.getByRole('dialog')).toContainText('1 of 20 seats assigned.');
 await page.getByRole('dialog').getByRole('button',{name:/^Reclaim seats ·/}).click();await page.getByRole('button',{name:'Reclaim seats',exact:true}).click();await expect(page.getByRole('dialog')).toContainText('0 of 20 seats assigned.');
});
test('assignment users use scoped search and readable account names',async({page})=>{
 await page.route('**/api/licenses/*/people?*',route=>route.fulfill({json:[{id:'outsider',provider:'ad',kind:0,name:'Alex Example',samAccountName:'alex.one'}]}));
 await open(page);await page.getByRole('button',{name:'+ Assign seats',exact:true}).click();await page.getByLabel('Search Active Directory',{exact:true}).fill('Alex');await page.getByRole('button',{name:'Search',exact:true}).click();
 await expect(page.getByLabel('Select result',{exact:true})).toContainText('Alex Example · alex.one · outsider');await page.getByLabel('Select result',{exact:true}).selectOption('outsider');
 await page.getByRole('button',{name:'Assign seats',exact:true}).click();await expect(page.getByRole('dialog')).toContainText('1 of 20 seats assigned.');
});
