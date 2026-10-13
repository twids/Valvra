const {test,expect}=require('@playwright/test');
test.use({locale:'en-GB'});

test('reader and editor see actionable capabilities and paths without administrative controls',async({page})=>{
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
  if(permissions===3){await expect(page.getByText('There are no accounts here yet. Someone with modify permission can add them.',{exact:true})).toBeVisible();await expect(page.getByText('There are no licenses here yet. Someone with modify permission can add them.',{exact:true})).toBeVisible();await expect(page.getByRole('button',{name:'+ Add password',exact:true})).toHaveCount(0);}
  else{await expect(page.locator('#page-description')).toContainText('You can add and edit content.');await expect(page.getByRole('button',{name:'+ Add password',exact:true})).toBeVisible();}
  await page.setExtraHTTPHeaders({});
 }
});

test('administration distinguishes global roles from resource access and names the active section',async({page})=>{
 await page.goto('/settings');await expect(page.getByRole('heading',{name:'Global roles',level:1})).toBeVisible();
 await expect(page.getByRole('heading',{name:'Resource access',level:2})).toBeVisible();
 await page.getByRole('link',{name:'Certificates & security',exact:true}).click();
 await expect(page.getByRole('heading',{name:'Certificates & security',level:1})).toBeFocused();await expect(page).toHaveTitle('Certificates & security · Valvra');
});

