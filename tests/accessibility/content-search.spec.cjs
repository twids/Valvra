const {test, expect} = require('@playwright/test');

test('an account can be located from the resources page using only its title', async ({page}) => {
    await page.goto('/resources');
    const vault = page.getByRole('searchbox', {name:'Sök i valvet'});
    await vault.fill('zzz-missing-account');
    await vault.press('Enter');
    const dialog = page.getByRole('dialog');
    const search = dialog.getByRole('searchbox', {name: /Kontotitel, produktnamn eller resursnamn/});
    await expect(search).toBeFocused();
    await expect(dialog.getByRole('status')).toHaveText('Inga träffar');
    await search.fill('ministratör');
    await search.press('Enter');
    await dialog.getByRole('button', {name: /Administratörskonto/}).first().click();
    await expect(page).toHaveURL(/\/resources\/[a-f0-9-]+$/);
    await expect(page.getByRole('searchbox', {name: 'Sök i resursen'})).toHaveValue('Administratörskonto');
});
