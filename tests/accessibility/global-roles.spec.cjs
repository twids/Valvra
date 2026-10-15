const {test,expect}=require('@playwright/test');
const {clickAction}=require('./ui.cjs');
test.use({locale:'en-GB'});
test('license administrator can use overview and assignments with no resource content grants',async({page})=>{
    await page.route('**/api/session',async route=>{const response=await route.fetch();const session=await response.json();await route.fulfill({response,json:{...session,isAccessAdministrator:false,isSystemAdministrator:false,isAuditor:false,isLicenseAdministrator:true}});});
    await page.route('**/api/resources',route=>route.fulfill({json:[]}));await page.route('**/api/groups',route=>route.fulfill({json:[]}));
    const destination={id:'11111111-1111-1111-1111-111111111111',name:'Licenses only',path:'Department / Licenses only',permissions:5,canOpenResource:false};
    const license={id:'22222222-2222-2222-2222-222222222222',resourceId:destination.id,product:'Global license',vendor:'Vendor',purchaseReference:'',seats:5,assignedSeats:0,revision:1,currentVersion:1};
    await page.route('**/api/licenses/destinations',route=>route.fulfill({json:[destination]}));
    await page.route('**/api/licenses/overview?*',route=>route.fulfill({json:{items:[license],hasMore:false}}));
    await page.route('**/api/resources/*/licenses',route=>route.fulfill({json:[license]}));
    await page.route('**/api/licenses/*/assignments',route=>route.fulfill({json:[]}));
    let secrets=0;page.on('request',r=>{if(/\/secrets|\/reveal/.test(r.url()))secrets++;});
    await page.goto('/licenses');const row=page.getByRole('row').filter({hasText:'Global license'});
    await expect(row.getByRole('button',{name:/Show key/})).toHaveCount(0);await expect(row.getByRole('link')).toHaveCount(0);
    await clickAction(row,/^Assignments/);await expect(page.getByRole('button',{name:'+ Assign seats',exact:true})).toBeVisible();
    await page.keyboard.press('Escape');await page.getByRole('button',{name:'+ Add license',exact:true}).click();
    await expect(page.getByLabel('Resource',{exact:true})).toContainText('Department / Licenses only');expect(secrets).toBe(0);
});
test('explicit exclusions require separate restoration confirmation',async({page})=>{
    const row={id:'11111111-1111-1111-1111-111111111111',targetKind:0,targetId:'22222222-2222-2222-2222-222222222222',targetName:'Excluded department',provider:'ad',subjectId:'user',createdAt:'2026-10-14T12:00:00Z',createdBy:'other',revision:1};
    await page.route('**/api/settings/exclusions',route=>route.fulfill({json:[row]}));let restores=0;
    await page.route('**/api/settings/exclusions/*/restore',async route=>{restores++;expect(route.request().postDataJSON()).toEqual({revision:1});await route.fulfill({status:204});});
    await page.goto('/settings');await expect(page.getByRole('heading',{name:'Explicit access exclusions',exact:true})).toBeVisible();
    expect(restores).toBe(0);await page.getByRole('button',{name:/^Restore access ·/}).click();
    await expect(page.getByRole('dialog')).toContainText('Other inherited exclusions must be restored separately.');expect(restores).toBe(0);
    await page.getByRole('dialog').getByRole('button',{name:'Restore access',exact:true}).click();await expect.poll(()=>restores).toBe(1);
});
