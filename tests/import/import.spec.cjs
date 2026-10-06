const {test,expect} = require('@playwright/test');
const AxeBuilder = require('@axe-core/playwright').default;
async function post(page,path,body) {
    return page.evaluate(async ({path,body}) => {
        const session=await (await fetch('/api/session')).json();
        const response=await fetch('/api'+path,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':session.csrfToken},body:JSON.stringify(body)});
        if(!response.ok)throw Error('Test preparation failed: '+response.status);
        return response.status===204?null:response.json();
    },{path,body});
}
async function file(page,text) {
    await page.getByLabel('CSV-fil (obligatoriskt)',{exact:true}).setInputFiles({name:'import.csv',mimeType:'text/csv',buffer:Buffer.from(text)});
    await page.getByRole('button',{name:'Läs fil',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Mappa kolumner',exact:true})).toBeVisible();
}
async function audit(page) {
    const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(x=>({id:x.id,nodes:x.nodes.map(n=>n.target)}))).toEqual([]);
}
test('real audited hierarchy import creates two levels, reuses groups on retry and never overwrites duplicate titles',async({page})=>{
    await expect.poll(async()=>{
        const response=await page.request.get('/api/resources');
        return response.ok() && (await response.json()).filter(x=>x.permissions===31).length;
    }).toBeGreaterThanOrEqual(3);
    await page.goto('/');
    await expect(page.getByRole('button',{name:'Importera lösenord',exact:true})).toBeVisible();
    const base=await post(page,'/groups',{name:'Import test '+Date.now(),parentId:null});
    await post(page,'/grants',{targetKind:0,targetId:base.id,subjectKind:0,provider:'ad',subjectId:'user',permissions:13});
    await page.reload();
    await page.getByRole('button',{name:'Importera lösenord',exact:true}).click();
    await file(page,'title,password,group,resource\nImported A,synthetic-import-secret,Ops/Servers,SQL\nImported B,other-test-secret,Ops/Servers,SQL');
    await page.getByLabel('Basgrupp',{exact:true}).selectOption(base.id);
    await audit(page);
    await page.getByRole('button',{name:'Förhandsgranska',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Förhandsgranskning',exact:true})).toBeVisible();
    await expect(page.locator('#dialog-content')).toContainText('2 poster. 2 nya grupper och 1 nya resurser.');
    await expect(page.locator('#dialog')).not.toContainText('synthetic-import-secret');
    await expect(page.locator('#dialog-content')).toContainText('Ops / Servers / SQL');
    await audit(page);
    await page.screenshot({path:'artifacts/import-preview.png'});
    await page.getByRole('button',{name:'Starta import',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Resultat',exact:true})).toBeVisible();
    await expect(page.locator('#dialog-content')).toContainText('Sparade: 2. Överhoppade: 0.');
    await page.getByRole('button',{name:'Stäng dialog',exact:true}).click();
    await page.getByRole('button',{name:'Importera lösenord',exact:true}).click();
    await file(page,'title,password,group,resource\nImported A,replacement-must-not-save,Ops/Servers,SQL');
    await page.getByLabel('Basgrupp',{exact:true}).selectOption(base.id);
    await page.getByRole('button',{name:'Förhandsgranska',exact:true}).click();
    await expect(page.locator('#dialog-content')).toContainText('0 nya grupper och 0 nya resurser.');
    await page.getByRole('button',{name:'Starta import',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Resultat',exact:true})).toBeVisible();
    await expect(page.locator('#dialog-content')).toContainText('Sparade: 0. Överhoppade: 1.');
    const state=await page.evaluate(async()=>({groups:await(await fetch('/api/groups')).json(),resources:await(await fetch('/api/resources')).json()}));
    const ops=state.groups.filter(x=>x.parentId===base.id&&x.name==='Ops');expect(ops).toHaveLength(1);
    const servers=state.groups.find(x=>x.parentId===ops[0].id&&x.name==='Servers');
    const resource=state.resources.find(x=>x.groupId===servers.id&&x.name==='SQL');
    const entries=await page.evaluate(async id=>(await fetch('/api/resources/'+id+'/secrets')).json(),resource.id);
    expect(entries.map(x=>[x.title,x.currentVersion])).toEqual([['Imported A',1],['Imported B',1]]);
    const events=await page.evaluate(async id=>(await fetch('/api/audit?resourceId='+id)).json(),resource.id);
    expect(events.some(x=>x.event.action==='Secret.Create'&&x.signatureValid)).toBe(true);
});
test('focus loss prevents unintended writes or retained import secrets',async({page})=>{
    await page.goto('/');
    const resources=await page.evaluate(async()=>(await fetch('/api/resources')).json());
    const target=resources.find(x=>x.name==='Databasplattform');
    await page.getByRole('button',{name:'Importera lösenord',exact:true}).click();
    await file(page,'title,password,group\nFocus test,never-post-this,External/Subgroup');
    await page.getByLabel('Destination',{exact:true}).selectOption('resource');
    await page.getByLabel('Resurs',{exact:true}).selectOption(target.id);
    await page.getByRole('button',{name:'Förhandsgranska',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Förhandsgranskning',exact:true})).toBeVisible();
    await page.evaluate(()=>window.dispatchEvent(new Event('blur')));
    await expect(page.locator('#dialog-error')).toHaveText('Importdata har tömts. Välj filen igen.');
    await expect(page.getByRole('button',{name:'Starta import',exact:true})).toBeDisabled();
    await page.getByRole('button',{name:'Stäng dialog',exact:true}).click();
    const entries=await page.evaluate(async id=>(await fetch('/api/resources/'+id+'/secrets')).json(),target.id);
    expect(entries.some(x=>x.title==='Focus test')).toBe(false);
});
test('a source branch maps to an existing group without creating its source hierarchy',async({page})=>{
    await page.goto('/');
    await expect(page.getByRole('button',{name:'Importera lösenord',exact:true})).toBeVisible();
    const base=await post(page,'/groups',{name:'Mapped target '+Date.now(),parentId:null});
    await post(page,'/grants',{targetKind:0,targetId:base.id,subjectKind:0,provider:'ad',subjectId:'user',permissions:13});
    const resource=await post(page,'/resources',{name:'Mapped R',groupId:base.id});
    await page.reload();
    await page.getByRole('button',{name:'Importera lösenord',exact:true}).click();
    await file(page,'title,password,group,resource\nMapped account,synthetic-mapped-secret,Old/Sub,Mapped R');
    await page.getByLabel('Old / Sub',{exact:true}).selectOption(base.id);
    await page.getByRole('button',{name:'Förhandsgranska',exact:true}).click();
    await expect(page.locator('#dialog-content')).toContainText('0 nya grupper och 0 nya resurser.');
    await expect(page.locator('#dialog-content')).toContainText('Mapped R');
    await expect(page.locator('#dialog-content')).not.toContainText('Old / Sub');
    await page.getByRole('button',{name:'Starta import',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Resultat',exact:true})).toBeVisible();
    await expect(page.locator('#dialog-content')).toContainText('Sparade: 1. Överhoppade: 0.');
    const entries=await page.evaluate(async id=>(await fetch('/api/resources/'+id+'/secrets')).json(),resource.id);
    expect(entries.map(x=>x.title)).toEqual(['Mapped account']);
});
test('English import mapping works on a narrow screen and previews source titles as plain text',async({page})=>{
    await page.goto('/');
    await page.getByLabel('Språk',{exact:true}).selectOption('en');
    await page.setViewportSize({width:360,height:800});
    await page.getByRole('button',{name:'Import passwords',exact:true}).click();
    await page.getByLabel('CSV file (required)',{exact:true}).setInputFiles({name:'untrusted.csv',mimeType:'text/csv',buffer:Buffer.from('title,password,group\n<img src=x>,synthetic-hidden-secret,One/Two/Three')});
    await page.getByRole('button',{name:'Read file',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Map columns',exact:true})).toBeVisible();
    await page.getByLabel('Destination',{exact:true}).selectOption('resource');
    await page.getByLabel('Resource',{exact:true}).selectOption({label:'IT och infrastruktur / Databasplattform'});
    await audit(page);
    await page.getByRole('button',{name:'Preview',exact:true}).click();
    await expect(page.getByRole('heading',{name:'Import · Preview',exact:true})).toBeVisible();
    await expect(page.locator('#dialog-content')).toContainText('<img src=x>');
    await expect(page.locator('#dialog-content img')).toHaveCount(0);
    await expect(page.locator('#dialog-content')).not.toContainText('synthetic-hidden-secret');
    await audit(page);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    await page.screenshot({path:'artifacts/import-mobile-preview.png'});
});
