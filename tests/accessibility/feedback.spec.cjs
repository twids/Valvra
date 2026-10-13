const {test, expect} = require('@playwright/test');
test.use({locale:'en-GB'});

test('success is dismissible, expires, and does not survive navigation', async ({page}) => {
    await page.goto('/');
    await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await expect(page.locator('#notice')).toBeVisible();
    await page.getByRole('button',{name:'Dismiss message',exact:true}).click();
    await expect(page.locator('#notice')).toBeHidden();
    await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await page.getByRole('link',{name:'License overview',exact:true}).click();
    await expect(page.locator('#notice')).toBeHidden();
    await page.clock.install();
    await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await page.getByRole('heading',{name:'License overview',level:1}).focus();
    await page.clock.fastForward(11000);
    await expect(page.locator('#notice')).toBeHidden();
});

test('errors persist until dismissed and render hostile text as text', async ({page}) => {
    await page.goto('/');
    await page.route('**/api/groups', route => route.fulfill({status:503,json:{error:'<img src=x onerror=alert(1)>'}}));
    await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await expect(page.locator('#page-error')).toContainText('<img');
    await expect(page.locator('#page-error img')).toHaveCount(0);
    await page.clock.install(); await page.clock.fastForward(30000);
    await expect(page.locator('#page-error')).toBeVisible();
    await page.getByRole('button',{name:'Dismiss message',exact:true}).click();
    await expect(page.locator('#page-error')).toBeHidden();
});

test('audit table is named for Events after a dialog has closed', async ({page}) => {
    await page.goto('/');
    await page.getByRole('link',{name:'Open resource · Databasplattform',exact:true}).click();
    await page.getByRole('button',{name:'Rename',exact:true}).click();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('link',{name:'Audit log',exact:true}).click();
    await expect(page.getByRole('table',{name:'Events',exact:true})).toBeVisible();
    await expect(page.getByRole('table',{name:'Rename',exact:true})).toHaveCount(0);
});
