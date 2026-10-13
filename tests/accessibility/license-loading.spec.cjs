const {test,expect}=require('@playwright/test');test.use({locale:'en-GB'});
test('overview for many resources uses aggregate metadata rather than per-resource requests',async({page})=>{
 let overview=0,perResource=0;page.on('request',r=>{if(/\/api\/resources\/[^/]+\/licenses$/.test(r.url()))perResource++;});
 await page.route('**/api/resources',async route=>{const response=await route.fetch(),rows=await response.json();await route.fulfill({response,json:Array.from({length:150},(_,i)=>({...rows[0],id:'resource-'+i,name:'Resource '+i}))});});
 await page.route('**/api/licenses/overview?*',route=>{overview++;return route.fulfill({json:{items:Array.from({length:150},(_,i)=>({id:'license-'+i,resourceId:'resource-'+i,product:'Product '+i,vendor:'',seats:1,assignedSeats:0})),hasMore:false}});});
 await page.goto('/licenses');await expect(page.getByRole('status')).toContainText('150 of 150 licenses shown');expect(overview).toBe(1);expect(perResource).toBe(0);
});
test('failed later page preserves marked partial results and retry resumes its offset',async({page})=>{
 let fail=true;const offsets=[];
 await page.route('**/api/licenses/overview?*',route=>{const offset=Number(new URL(route.request().url()).searchParams.get('offset'));offsets.push(offset);
 if(offset===200&&fail)return route.fulfill({status:503,json:{error:'Synthetic page failure'}});
 return route.fulfill({json:{items:Array.from({length:offset===0?200:1},(_,i)=>({id:'license-'+(offset+i),resourceId:'resource',product:'Product '+(offset+i),vendor:'',seats:1,assignedSeats:0})),hasMore:offset===0}});
 });
 await page.goto('/licenses');await expect(page.getByText('Partial overview: summaries, search and sorting cover only loaded licenses.',{exact:true})).toBeVisible();await expect(page.getByRole('status')).toContainText('200 of 200');
 fail=false;await page.getByRole('button',{name:'Retry',exact:true}).click();await expect(page.getByRole('status')).toContainText('201 of 201');expect(offsets).toEqual([0,200,200]);
});

test('retry respects the absolute overview cap and does not offer a permanent invalid-offset retry',async({page})=>{
 let fail=true;const offsets=[];const items=Array.from({length:200},(_,i)=>({id:'license-'+i,resourceId:'resource',product:'Product '+i,vendor:'',seats:1,assignedSeats:0}));
 await page.route('**/api/licenses/overview?*',route=>{const offset=Number(new URL(route.request().url()).searchParams.get('offset'));offsets.push(offset);if(offset===200&&fail)return route.fulfill({status:503,json:{error:'Synthetic failure'}});return route.fulfill({json:{items,hasMore:true}});});
 await page.goto('/licenses');await expect(page.getByRole('button',{name:'Retry',exact:true})).toBeVisible();fail=false;await page.getByRole('button',{name:'Retry',exact:true}).click();
 await expect(page.getByText('Partial overview: summaries, search and sorting cover only loaded licenses.',{exact:true})).toBeVisible();await expect(page.getByRole('button',{name:'Retry',exact:true})).toHaveCount(0);
 expect(Math.max(...offsets)).toBe(19800);
});
