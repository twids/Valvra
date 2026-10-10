const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
function model(groups,resources=[]) {
    const context={window:{ValvraNavigation:require('../../src/Valvra.Web/wwwroot/js/navigation.js'),ValvraI18n:{locale:'sv-SE'},addEventListener(){}},document:{getElementById:()=>({addEventListener(){}}),querySelectorAll:()=>[],addEventListener(){}},setInterval(){}};
    let source=fs.readFileSync('src/Valvra.Web/wwwroot/js/app.js','utf8').split('\n').filter(x=>!x.includes('safely(async () => { await i18n.ready;')).join('\n');
    source=source.replace(/\}\)\(\);\s*$/,'globalThis.groupModel = {state,groupPath,groupIds,scopedResources};})();');
    vm.runInNewContext(source,context);
    const result=context.groupModel; result.state.groups=groups; result.state.resources=resources; return result;
}
const groups=[{id:'root',parentId:null,name:'Organisation'},{id:'child',parentId:'root',name:'Servrar'},{id:'deep',parentId:'child',name:'Produktion'},{id:'other',parentId:null,name:'Servrar'}];
const resources=[{id:'a',groupId:'root',name:'SQL'},{id:'b',groupId:'child',name:'Windows'},{id:'c',groupId:'deep',name:'Linux'},{id:'d',groupId:'other',name:'Windows'}];
test('group scope includes all descendants by ID and keeps same-named unrelated groups separate',()=>{
    const h=model(groups,resources);
    assert.deepEqual(Array.from(h.scopedResources('root',true),x=>x.id),['a','b','c']);
    assert.deepEqual(Array.from(h.scopedResources('child',true),x=>x.id),['b','c']);
    assert.deepEqual(Array.from(h.scopedResources('other',true),x=>x.id),['d']);
});
test('direct-only scope and search compose without broadening visible metadata',()=>{
    const h=model(groups,resources);
    assert.deepEqual(Array.from(h.scopedResources('root',false),x=>x.id),['a']);
    assert.deepEqual(Array.from(h.scopedResources('root',true,'WINDOWS'),x=>x.id),['b']);
    assert.deepEqual(Array.from(h.scopedResources('unknown',true),x=>x.id),[]);
});
test('missing parent information remains absent from paths and cannot connect hidden branches',()=>{
    const h=model([{id:'visible',parentId:'hidden',name:'Visible'}],[{id:'permitted',groupId:'visible',name:'Account'}]);
    assert.deepEqual(Array.from(h.groupPath('visible'),x=>x.name),['Visible']);
    assert.deepEqual(Array.from(h.groupPath('hidden')),[]);
    assert.deepEqual(Array.from(h.scopedResources('hidden',true)),[]);
});
test('cycle guards terminate and long valid hierarchies retain the complete permitted scope',()=>{
    const cycle=model([{id:'a',parentId:'b',name:'A'},{id:'b',parentId:'a',name:'B'}]);
    assert.equal(cycle.groupIds('a',true).size,2); assert.equal(cycle.groupPath('a').length,2);
    const chain=Array.from({length:128},(_,index)=>({id:String(index),parentId:index?String(index-1):null,name:String(index)}));
    const h=model(chain,[{id:'last',groupId:'127',name:'Last'}]);
    assert.equal(h.groupPath('127').length,128); assert.equal(h.groupIds('0',true).size,128);
    assert.equal(h.scopedResources('0',true).length,1);
});
