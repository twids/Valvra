// Follow the same visible menus as a user; don't invoke application internals.
async function clickAction(root, name) {
    const direct=root.getByRole('button',{name,exact:typeof name==='string'});
    if(await direct.isVisible())return direct.click();
    const item=root.getByRole('menuitem',{name,exact:typeof name==='string',includeHidden:true});
    await item.locator('..').locator('..').locator(':scope > button').click();
    await item.click();
}
module.exports={clickAction};
