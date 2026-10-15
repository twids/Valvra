const {test,expect}=require('@playwright/test');
test.use({locale:'en-GB'});

test('reader and editor see actionable capabilities and paths without administrative controls',async({page})=>{
 await page.setViewportSize({width:390,height:844});
 const session=await(await page.request.get('/api/session')).json(),headers={'X-CSRF-TOKEN':session.csrfToken};
 const groups=await(await page.request.get('/api/groups')).json();
 const resources=await(await page.request.get('/api/resources')).json(),{id}=resources.find(x=>x.name==='Databasplattform');
 await page.route('**/api/resources/*/secrets',route=>route.fulfill({json:[]}));
 await page.route('**/api/resources/*/licenses',route=>route.fulfill({json:[]}));
 for(const [identity,permissions] of [['journey-reader',3],['journey-editor',7]]){
  expect((await page.request.post('/api/grants',{headers,data:{targetKind:1,targetId:id,subjectKind:0,provider:'ad',subjectId:identity,permissions}})).ok()).toBe(true);
  expect((await page.request.post('/api/grants',{headers,data:{targetKind:0,targetId:groups[0].id,subjectKind:0,provider:'ad',subjectId:identity,permissions:1}})).ok()).toBe(true);
  await page.setExtraHTTPHeaders({'X-Test-Identity':identity});await page.goto('/resources/'+id);
  await expect(page.locator('#page-description')).toContainText('You can reveal and copy secrets.');
  await expect(page.getByRole('navigation',{name:'Group path'})).toContainText('IT och infrastruktur');
  await expect(page.getByRole('link',{name:'Administration',exact:true})).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
  if(permissions===3){await expect(page.getByText('There are no accounts here yet. Someone with modify permission can add them.',{exact:true})).toBeVisible();await expect(page.getByText('There are no licenses here yet. Someone with modify permission can add them.',{exact:true})).toBeVisible();await expect(page.getByRole('button',{name:'+ Add password',exact:true})).toBeDisabled();}
  else{await expect(page.locator('#page-description')).toContainText('You can add and edit content.');await expect(page.getByRole('button',{name:'+ Add password',exact:true})).toBeVisible();}
  await page.setExtraHTTPHeaders({});
 }
});

test('access administration grants no content rights and system-only administration hides global roles',async({page})=>{
 const session=await(await page.request.get('/api/session')).json(),headers={'X-CSRF-TOKEN':session.csrfToken};
 const resources=await(await page.request.get('/api/resources')).json(),resource=resources.find(x=>x.name==='Databasplattform');
 for(const [subjectId,roles]of [['journey-access-admin',32],['journey-system-admin',64]])expect((await page.request.post('/api/settings/roles',{headers,data:{provider:'ad',subjectId,kind:0,roles,revision:0}})).ok()).toBe(true);
 let contentReads=0;page.on('request',r=>{if(new RegExp('/api/resources/'+resource.id+'/(secrets|licenses)$').test(r.url()))contentReads++;});
 await page.setExtraHTTPHeaders({'X-Test-Identity':'journey-access-admin'});await page.goto('/resources/'+resource.id);
 await expect(page.getByText('You manage access',{exact:true})).toBeVisible();await expect(page.getByRole('button',{name:'+ Add password',exact:true})).toBeDisabled();expect(contentReads).toBe(0);
 await page.setExtraHTTPHeaders({'X-Test-Identity':'journey-system-admin'});await page.goto('/settings');
 await expect(page.getByRole('heading',{name:'Sign-in and directory',level:1})).toBeVisible();await expect(page.getByRole('link',{name:'Global roles',exact:true})).toHaveCount(0);
 await page.goto('/settings/people');await expect(page.getByRole('heading',{name:'This view is unavailable',level:1})).toBeVisible();
});

test('administration distinguishes global roles from resource access and names the active section',async({page})=>{
 await page.goto('/settings');await expect(page.getByRole('heading',{name:'Global roles',level:1})).toBeVisible();
 await expect(page.getByRole('heading',{name:'Resource access',level:2})).toBeVisible();
 await page.getByRole('link',{name:'Certificates & security',exact:true}).click();
 await expect(page.getByRole('heading',{name:'Certificates & security',level:1})).toBeFocused();await expect(page).toHaveTitle('Certificates & security · Valvra');
});

test('resource metadata filtering preserves context and never reads secret payloads',async({page})=>{
 let reveals=0;page.on('request',r=>{if(r.url().includes('/reveal'))reveals++;});
 await page.goto('/');await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
 const path=new URL(page.url()).pathname,search=page.getByLabel('Search this resource',{exact:true});
 await search.fill('Exempellicens');await expect(page.getByRole('table',{name:'Licenses',exact:true})).toBeVisible();
 await expect(page.getByText('No account titles match your search.',{exact:true})).toBeVisible();
 await page.getByRole('link',{name:'License overview',exact:true}).click();await page.goBack();
 await expect(page).toHaveURL(path);await expect(search).toHaveValue('Exempellicens');
 await search.fill('synthetic-browser-test-password');await expect(page.getByText('No product names match your search.',{exact:true})).toBeVisible();expect(reveals).toBe(0);
 await page.getByRole('button',{name:'Clear filters',exact:true}).click();await expect(search).toHaveValue('');await expect(page.getByRole('table',{name:'Password',exact:true})).toBeVisible();
});
