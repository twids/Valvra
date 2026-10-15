const {test,expect}=require('@playwright/test');test.use({locale:'en-GB'});
async function resource(page){await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();}
test('direct grants can be edited without retargeting the identity',async({page})=>{
 let posted;await page.route('**/api/grants/*/update',route=>{posted=route.request().postDataJSON();return route.fulfill({status:204});});
 await resource(page);await page.getByRole('button',{name:'Permissions',exact:true}).click();
 await page.getByRole('button',{name:/^Edit access ·/}).click();await expect(page.getByRole('dialog')).toContainText('user');
 await expect(page.getByLabel('Search Active Directory',{exact:true})).toHaveCount(0);
 await page.getByLabel('Read secrets',{exact:true}).uncheck();await page.getByRole('button',{name:'Save',exact:true}).click();
 expect(posted).toMatchObject({permissions:29,revision:1});expect(posted.subjectId).toBeUndefined();expect(posted.targetId).toBeUndefined();
 await expect(page.getByRole('dialog',{name:'Permissions · Databasplattform',exact:true})).toBeVisible();
});
test('one failed content read retains Add actions and the other content section',async({page})=>{
 await page.route('**/api/resources/*/secrets',route=>route.fulfill({status:503,json:{error:'Synthetic password-list failure'}}));
 await resource(page);await expect(page.getByRole('button',{name:'+ Add password',exact:true})).toBeEnabled();
 await expect(page.getByRole('table',{name:'Licenses',exact:true})).toBeVisible();await expect(page.getByText('Synthetic password-list failure',{exact:true})).toBeVisible();
 await page.getByRole('button',{name:'+ Add password',exact:true}).click();await expect(page.getByRole('dialog',{name:'Add password',exact:true})).toBeVisible();
});
for(const interval of [{permissions:4,startsAt:'2026-12-01T10:00:15.123Z',expiresAt:null},{permissions:3,startsAt:'2026-12-01T10:00:15.123Z',expiresAt:'2026-12-01T10:00:45.456Z'}])test('unchanged grant preserves exact rights and interval '+interval.permissions,async({page})=>{
 let posted;await page.route('**/api/access/1/*',async route=>{const response=await route.fetch(),value=await response.json();Object.assign(value.grants[0],interval);await route.fulfill({response,json:value});});
 await page.route('**/api/grants/*/update',route=>{posted=route.request().postDataJSON();return route.fulfill({status:204});});
 await resource(page);await page.getByRole('button',{name:'Permissions',exact:true}).click();await page.getByRole('button',{name:/^Edit access ·/}).click();await page.getByRole('button',{name:'Save',exact:true}).click();
 expect(posted).toMatchObject(interval);
});
