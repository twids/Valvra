const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const {webcrypto} = require('node:crypto');
class Element {
    constructor(tag='div') { this.tag=tag; this.children=[]; this.dataset={}; this.style={}; this.value=''; this.type=''; this.open=false; this.events={}; this.classList={add(){},toggle(){}}; }
    append(...nodes) { this.children.push(...nodes); if(this.tag==='select' && !this.value) this.value=this.children[0]?.value || ''; }
    replaceChildren(...nodes) { this.children=[...nodes]; }
    addEventListener(name, action) { this.events[name]=action; }
    setAttribute() {}
    removeAttribute() {}
    focus() {}
    querySelector(selector) { return this.querySelectorAll(selector)[0]; }
    close() { this.open=false; }
    showModal() { this.open=true; }
    querySelectorAll(selector) { return this.children.filter(x=>x instanceof Element).flatMap(x=>[x,...x.querySelectorAll('*')]).filter(x=>selector==='*'||selector.split(',').includes(x.tag)); }
}
function harness(audit = null) {
    const elements=new Map(), handlers=new Map(), writes=[];
    let complete, now=Date.now();
    const pending=new Promise(resolve=>complete=resolve);
    const context={Node:Element,crypto:webcrypto,URLSearchParams,console,Date:class extends Date{static now(){return now;}},clearTimeout(){},setTimeout(){return 1;},setInterval(fn){handlers.set('inactivity',fn);},
        document:{hidden:false,hasFocus:()=>true,getElementById:id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);},createElement:tag=>new Element(tag),createTextNode:x=>x,querySelectorAll:()=>[],addEventListener:(name,fn)=>handlers.set(name,fn)},
        window:{ValvraNavigation:require('../../src/Valvra.Web/wwwroot/js/navigation.js'),ValvraI18n:{message:key=>key,locale:'sv-SE',ready:Promise.resolve(),setText:(element,text)=>element.textContent=text},addEventListener:(name,fn)=>handlers.set(name,fn)},
        navigator:{clipboard:{writeText:async value=>writes.push({clipboard:value})}},
        fetch:async(path,options)=>{
            if(path.endsWith('/reveal'))return pending;
            if(audit && path==='/api/audit/targets')return audit.targetsUnavailable ? {ok:false,status:503,json:async()=>({error:'Unavailable'})} : {ok:true,status:200,json:async()=>({resources:[],groups:[],invalidEventCount:audit.invalidCount||0,...audit.targets})};
            if(audit && path.startsWith('/api/audit?'))return {ok:true,status:200,json:async()=>audit.events};
            if(!options.body)return {ok:true,status:200,json:async()=>[]};
            writes.push({path,body:JSON.parse(options.body)});return{ok:true,status:204};}};
    let source=fs.readFileSync('src/Valvra.Web/wwwroot/js/app.js','utf8').split('\n').filter(x=>!x.includes('safely(async () => { await i18n.ready;')).join('\n');
    source=source.replace(/\}\)\(\);\s*$/,'globalThis.unit = { state, licenseDialog, revealLicense, revealSecret, copySecret, closeDialog, auditPage, auditScopeLabel }; })();');
    vm.runInNewContext(source,context);
    context.unit.state.session={csrfToken:'synthetic-token'}; context.unit.state.resource={id:'resource',permissions:7};context.unit.state.view='groups';
    return{context,elements,handlers,writes,complete,advance:ms=>now+=ms,ui:context.unit,license:{id:'license',product:'Product',vendor:'Vendor',purchaseReference:'ref',seats:2,revision:1}};
}
test('metadata editing defaults to preserving key without fetching or submitting plaintext',async()=>{
    const h=harness();h.ui.licenseDialog(h.license);
    await h.elements.get('dialog-form').onsubmit({preventDefault(){}});
    assert.equal(h.writes.length,1);assert.equal(h.writes[0].body.secretChange,0);assert.equal(h.writes[0].body.payload,null);
});
test('blur preserves ordinary license edits and metadata can be saved on return',async()=>{
    const h=harness();h.ui.licenseDialog(h.license);
    h.elements.get('dialog-content').querySelectorAll('input')[0].value='Edited product';h.handlers.get('blur')();
    assert.equal(h.elements.get('dialog-error').textContent,'');
    await h.elements.get('dialog-form').onsubmit({preventDefault(){}});
    assert.equal(h.writes.length,1);assert.equal(h.writes[0].body.product,'Edited product');assert.equal(h.writes[0].body.secretChange,0);assert.equal(h.writes[0].body.payload,null);
});
test('blur clears replacement secrets and blocks save while retaining ordinary inputs',async()=>{
    const h=harness();h.ui.licenseDialog(h.license);
    const fields=h.elements.get('dialog-content').querySelectorAll('input,textarea');const mode=h.elements.get('dialog-content').querySelectorAll('select')[0];
    mode.value='1';mode.events.change();const key=fields.find(x=>x.type==='password');key.value='new-synthetic-key';fields[0].value='Edited product';h.handlers.get('blur')();
    assert.equal(h.elements.get('dialog').open,true);assert.equal(fields[0].value,'Edited product');assert.equal(key.value,'');
    assert.equal(h.elements.get('dialog-actions').querySelectorAll('button').find(x=>x.type==='submit').disabled,true);
    await h.elements.get('dialog-form').onsubmit({preventDefault(){}});assert.equal(h.writes.length,0);
    assert.equal(h.elements.get('dialog-actions').querySelectorAll('button').find(x=>x.type==='submit').disabled,true);
    key.value='new-synthetic-key';key.events.input();assert.equal(h.elements.get('dialog-actions').querySelectorAll('button').find(x=>x.type==='submit').disabled,false);
});
test('replacement requires a nonempty new key, while clearing is explicit',async()=>{
    const h=harness();h.ui.licenseDialog(h.license);
    const mode=h.elements.get('dialog-content').querySelectorAll('select')[0];mode.value='1';mode.events.change();
    await h.elements.get('dialog-form').onsubmit({preventDefault(){}});assert.equal(h.writes.length,0);
    assert.match(h.elements.get('dialog-error').textContent,/ny licensnyckel/);
    mode.value='2';mode.events.change();await h.elements.get('dialog-form').onsubmit({preventDefault(){}});
    assert.equal(h.writes[0].body.secretChange,2);assert.equal(h.writes[0].body.payload,null);
});
test('inactivity clears secrets and blocks replacement while retaining ordinary draft fields',()=>{
    const h=harness();h.ui.licenseDialog(h.license);
    const fields=h.elements.get('dialog-content').querySelectorAll('input,textarea');const mode=h.elements.get('dialog-content').querySelectorAll('select')[0];
    mode.value='1';mode.events.change();fields[0].value='Edited product';fields.find(x=>x.type==='password').value='synthetic-key';
    h.advance(301000);h.handlers.get('inactivity')();
    assert.equal(h.elements.get('dialog').open,true);assert.equal(fields[0].value,'Edited product');
    assert.equal(fields.find(x=>x.type==='password').value,'');
    assert.equal(h.elements.get('dialog-actions').querySelectorAll('button').find(x=>x.type==='submit').disabled,true);
});
test('inactivity closes a revealed secret and removes its sensitive fields',async()=>{
    const h=harness();const pending=h.ui.revealLicense(h.license);
    h.complete({ok:true,status:200,json:async()=>({licenseKey:'synthetic-key',notes:'note'})});await pending;
    assert.equal(h.elements.get('dialog').open,true);
    h.advance(301000);h.handlers.get('inactivity')();
    assert.equal(h.elements.get('dialog').open,false);assert.equal(h.elements.get('dialog-content').children.length,0);
});
for(const action of ['revealLicense','revealSecret','copySecret'])test(`late ${action} response cannot expose values after focus loss`,async()=>{
    const h=harness();const request=h.ui[action](h.license);h.context.document.hidden=true;h.handlers.get('blur')();
    h.complete({ok:true,status:200,json:async()=>({licenseKey:'synthetic-late-key',password:'synthetic-late-password',username:'u',notes:'note'})});await request;
    assert.equal(h.elements.get('dialog').open,false);assert.equal(h.elements.get('dialog-content').children.length,0);assert.equal(h.writes.length,0);
});
test('a late response cannot replace a newer dialog',async()=>{
    const h=harness();const pending=h.ui.revealLicense(h.license);h.ui.licenseDialog(h.license);
    h.complete({ok:true,status:200,json:async()=>({licenseKey:'synthetic-late-key',notes:''})});await pending;
    assert.equal(h.elements.get('dialog-title').textContent,'Ändra licens');
    assert.ok(h.elements.get('dialog-content').querySelectorAll('input,textarea').every(x=>x.value!=='synthetic-late-key'));
});

test('audit rendering survives malformed scopes and hides unverified metadata',async()=>{
    const event={timestamp:'2026-10-05T10:00:00Z',actorId:'user',action:'Audit.InvalidPayload',outcome:'InvalidSignature',scope:{resourceName:'Untrusted name',groupPath:null}};
    const h=harness({events:[{event,signatureValid:false}],invalidCount:1});
    await h.ui.auditPage();
    assert.equal(h.elements.get('content').querySelectorAll('table').length,1);
    assert.equal(h.ui.auditScopeLabel({event,signatureValid:false}),'Auditkontext kan inte verifieras.');
    for(const path of [null,undefined,{},[]])assert.equal(h.ui.auditScopeLabel({event:{scope:{groupPath:path}},signatureValid:true}),'Auditkontext kan inte verifieras.');
    assert.ok(h.elements.get('content').querySelectorAll('p').some(x=>String(x.textContent).includes('Korrupta auditrader')));
});

test('audit list remains available when filter choices fail',async()=>{
    const h=harness({targetsUnavailable:true,events:[{event:{timestamp:'2026-10-05T10:00:00Z',actorId:'user',action:'Test',outcome:'Success',scope:null},signatureValid:true}]});
    await h.ui.auditPage();
    assert.equal(h.elements.get('content').querySelectorAll('table').length,1);
    assert.ok(h.elements.get('content').querySelectorAll('select').every(x=>x.disabled));
    assert.ok(h.elements.get('content').querySelectorAll('p').some(x=>String(x.textContent).includes('Filterval kunde inte läsas')));
});

test('audit choices disambiguate duplicate names using their exact IDs',async()=>{
    const h=harness({events:[],targets:{resources:[{id:'resource-a',name:'Shared name'},{id:'resource-b',name:'Shared name'}]}});
    await h.ui.auditPage();
    const options=h.elements.get('content').querySelectorAll('option').filter(x=>x.value.startsWith('resource-'));
    assert.deepEqual(options.map(x=>String(x.textContent)),['Shared name · resource-a','Shared name · resource-b']);
    assert.deepEqual(options.map(x=>x.value),['resource-a','resource-b']);
});
