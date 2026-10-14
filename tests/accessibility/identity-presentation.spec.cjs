const {test,expect}=require('@playwright/test');
const {clickAction}=require('./ui.cjs');
test.use({locale:'en-GB'});
const sid='S-1-5-21-123456789-234567890-345678901-1101';
const person={id:sid,provider:'ad',kind:0,name:'Alex Example',samAccountName:'alex.one'};
async function noSid(page){expect(await page.locator('body').innerText()).not.toMatch(/S-\d-(?:\d+-)*\d+/i);}
async function resource(page){await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();}
test('active people populate recipient choices without selection or authority and submit stable identity',async({page})=>{
    await page.route('**/api/directory/active',route=>route.fulfill({json:[person]}));let posted;
    await page.route('**/api/grants',route=>{posted=route.request().postDataJSON();return route.fulfill({json:{id:'synthetic'}});});
    await resource(page);await page.getByRole('button',{name:'Permissions',exact:true}).click();await page.getByRole('button',{name:'+ Grant access',exact:true}).click();
    const choices=page.getByLabel('Select result',{exact:true});await expect(choices).toContainText('Alex Example · alex.one');await expect(choices).toHaveValue('');
    await noSid(page);await page.getByRole('button',{name:'Grant',exact:true}).click();expect(posted).toBeUndefined();
    await choices.selectOption(sid);await noSid(page);await page.getByRole('button',{name:'Grant',exact:true}).click();
    await expect.poll(()=>posted?.subjectId).toBe(sid);
});
test('version history shows account names and never exposes unresolved recorded SIDs',async({page})=>{
    await page.route('**/api/secrets/*/versions',route=>route.fulfill({json:[
        {version:2,createdAt:'2026-10-14T10:00:00Z',createdBy:sid,createdByName:person.name,samAccountName:person.samAccountName},
        {version:1,createdAt:'2026-10-13T10:00:00Z',createdBy:sid+'9'}]}));
    await resource(page);await clickAction(page,'History · Administratörskonto');
    await expect(page.getByRole('dialog')).toContainText('Alex Example · alex.one');await expect(page.getByRole('dialog')).toContainText('Unknown or removed account');await noSid(page);
});
test('global role editor suggests active people without leaking SID in selection feedback',async({page})=>{
    await page.route('**/api/settings/people/active',route=>route.fulfill({json:[person]}));
    await page.goto('/settings');await page.getByRole('button',{name:'+ Assign user account',exact:true}).click();
    await page.getByRole('button',{name:'Alex Example · alex.one',exact:true}).click();
    await expect(page.getByRole('dialog').getByRole('status')).toHaveText('Alex Example · alex.one');await noSid(page);
});
test('audit actors and expanded technical details show account names instead of SID',async({page})=>{
    await page.route('**/api/audit/targets',route=>route.fulfill({json:{resources:[],groups:[],invalidEventCount:0,actors:[{id:sid,provider:'ad',name:person.name,samAccountName:person.samAccountName}],actions:['Secret.Create']}}));
    await page.route('**/api/audit?*',route=>route.fulfill({json:[{event:{id:'event',operationId:'operation',timestamp:'2026-10-14T10:00:00Z',actorProvider:'ad',actorId:sid,action:'Secret.Create',phase:2,outcome:'Committed',correlationId:'correlation',scope:null},signatureValid:true}]}));
    await page.goto('/audit');await expect(page.getByRole('table')).toContainText('Alex Example · alex.one');
    await page.getByRole('table').locator('summary').click();await page.getByText('Advanced filters',{exact:true}).click();await noSid(page);
});
test('global roles, exclusions and the signed-in label use account names',async({page})=>{
    await page.route('**/api/session',async route=>{const response=await route.fetch();const value=await response.json();await route.fulfill({response,json:{...value,subjectId:sid,displayName:sid,samAccountName:'alex.one'}});});
    await page.route('**/api/settings/roles',route=>route.fulfill({json:[{id:'role',provider:'ad',subjectId:sid,name:sid,samAccountName:'alex.one',roles:256,revision:1}]}));
    await page.route('**/api/settings/exclusions',route=>route.fulfill({json:[{id:'exclusion',targetKind:0,targetId:'group',targetName:'Department',provider:'ad',subjectId:sid,name:person.name,samAccountName:'alex.one',createdBy:sid,createdByName:person.name,createdBySamAccountName:'alex.one',createdAt:'2026-10-14T10:00:00Z',revision:1}]}));
    await page.goto('/settings');await expect(page.locator('#identity')).toHaveText('alex.one');await expect(page.locator('#content')).toContainText('Alex Example · alex.one');await noSid(page);
    await page.getByRole('button',{name:/^Restore access ·/}).click();await noSid(page);
});
test('permissions and license assignment rows keep SID out of visible identity details',async({page})=>{
    await page.route('**/api/access/*/*',async route=>{const response=await route.fetch();const value=await response.json();value.grants=value.grants.map(x=>({...x,subjectId:sid,subjectKind:0}));value.subjects=[person];await route.fulfill({response,json:value});});
    await page.route('**/api/licenses/*/assignments',route=>route.fulfill({json:[{id:'assignment',userId:sid,userProvider:'ad',userName:person.name,samAccountName:'alex.one',seats:1,createdAt:'2026-10-14T10:00:00Z'}]}));
    await resource(page);await page.getByRole('button',{name:'Permissions',exact:true}).click();await expect(page.getByRole('dialog')).toContainText('Alex Example · alex.one');await noSid(page);
    await page.keyboard.press('Escape');await clickAction(page.getByRole('row').filter({hasText:'Exempellicens'}),/^Assignments/);await expect(page.getByRole('dialog')).toContainText('Alex Example · alex.one');await noSid(page);
});
