const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const {webcrypto} = require('node:crypto');
class Element {
    constructor(tag='div') { this.tag=tag; this.children=[]; this.dataset={}; this.style={}; this.value=''; this.type=''; this.open=false; this.events={}; this.attributes={}; this.classList={add(){},toggle(){}}; }
    append(...nodes) { this.children.push(...nodes); nodes.forEach(x=>{if(x instanceof Element)x.parentElement=this;}); if(this.tag==='select' && !this.value) this.value=this.children[0]?.value || ''; }
    insertBefore(child,before) { const index=this.children.indexOf(before); if(index<0)throw new Error('Missing child'); this.children.splice(index,0,child); child.parentElement=this; }
    replaceChildren(...nodes) { this.children.forEach(x=>{if(x instanceof Element)x.parentElement=null;}); this.children=[]; this.append(...nodes); }
    remove(){if(this.parentElement)this.parentElement.children=this.parentElement.children.filter(x=>x!==this);this.parentElement=null;}
    closest(selector) { for(let node=this;node;node=node.parentElement)if(selector.split(',').includes(node.tag))return node; return null; }
    addEventListener(name, action) { this.events[name]=action; }
    setAttribute(name,value) {this.attributes[name]=value;}
    removeAttribute(name) {delete this.attributes[name];}
    hasAttribute(name) {return Object.hasOwn(this.attributes,name);}
    focus() {}
    get labels() { let root=this;while(root.parentElement)root=root.parentElement;return root.querySelectorAll('label').filter(x=>x.htmlFor===this.id); }
    querySelector(selector) { return this.querySelectorAll(selector)[0]; }
    close() { this.open=false; }
    showModal() { this.open=true; }
    querySelectorAll(selector) { return this.children.filter(x=>x instanceof Element).flatMap(x=>[x,...x.querySelectorAll('*')]).filter(x=>selector==='*'||selector.split(',').includes(x.tag)); }
}
function harness(audit = null) {
    const elements=new Map(), handlers=new Map(), writes=[];
    let complete, now=Date.now();
    const pending=new Promise(resolve=>complete=resolve);
    const context={Node:Element,crypto:webcrypto,URLSearchParams,console,Date:class extends Date{static now(){return now;}},clearTimeout(){},clearInterval(){},setTimeout(){return 1;},setInterval(fn,delay){handlers.set(delay===10000?'inactivity':'countdown',fn);},
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
    source=source.replace(/\}\)\(\);\s*$/,'globalThis.unit = { grantValidity, accountCopy, state, secretDialog, licenseDialog, licenseRecycleDialog, revealLicense, revealSecret, copySecret, closeDialog, auditPage, auditScopeLabel, resourcePage, groupsPage, contentSearchDialog, resourceCards, resourcesPage }; })();');
    vm.runInNewContext(source,context);
    context.unit.state.session={csrfToken:'synthetic-token'}; context.unit.state.resource={id:'resource',permissions:7};context.unit.state.view='groups';
    return{context,elements,handlers,writes,complete,advance:ms=>now+=ms,ui:context.unit,license:{id:'license',product:'Product',vendor:'Vendor',purchaseReference:'ref',seats:2,revision:1}};
}
test('group search filters visible metadata and restores the original tree', () => {
    const h = harness();
    h.ui.state.groups = [{id:'a', name:'Produktion'}, {id:'b', name:'Utveckling'}];
    h.ui.groupsPage();
    const content = h.elements.get('content');
    const search = content.querySelector('input');
    const tree = content.querySelector('ul');
    search.value = '  PRODUK  ';
    search.events.input();
    assert.equal(tree.parentElement.hidden, true);
    const results = content.querySelectorAll('ul')[1];
    assert.deepEqual(results.querySelectorAll('a').map(link => link.href), ['/groups/a']);
    search.value = '';
    search.events.input();
    assert.equal(tree.parentElement.hidden, false);
    assert.deepEqual(content.querySelectorAll('ul'), [tree]);
});
test('a failed metadata list cannot produce a complete or empty resource count', async () => {
    for (const bothFail of [false, true]) {
        const h = harness();
        h.ui.state.view = 'vault';
        h.ui.state.resource = {id:'resource', name:'Test', permissions:1};
        h.context.fetch = async path => bothFail || path.endsWith('/secrets')
            ? {ok:false, status:503, headers:{get:() => null}, json:async () => ({error:'Unavailable'})}
            : {ok:true, status:200, json:async () => []};
        await h.ui.resourcePage();
        const status = h.elements.get('content').querySelectorAll('p').find(p => p.attributes.role === 'status');
        assert.match(status.textContent, bothFail ? /Inga listor kunde hämtas/ : /Ofullständigt resultat/);
    }
});
test('switching recycle bins drops old rows and ignores an earlier response even after switching back', async () => {
    const h = harness(), pending = [];
    h.context.fetch = async path => {
        const rows = await new Promise(resolve => pending.push({path, resolve}));
        return {ok:true, json:async () => rows};
    };
    h.ui.licenseRecycleDialog([{id:'a',name:'A'}, {id:'b',name:'B'}]);
    const body = h.elements.get('dialog-content'), picker = body.querySelector('select');
    const select = id => { picker.value = id; return picker.events.change(); };
    const first = select('a');
    const second = select('b');
    const last = select('a');
    pending[2].resolve([{id:'new',product:'New',seats:1,assignedSeats:0}]);
    await last;
    pending[0].resolve([]); pending[1].resolve([]);
    await Promise.all([first, second]);
    assert.equal(body.querySelectorAll('table').length, 1);
    const reset = select('b');
    assert.equal(body.querySelectorAll('table').length, 0);
    pending[3].resolve([]); await reset;
    assert.equal(body.querySelector('p').textContent, 'Papperskorgen är tom.');
    assert.deepEqual(pending.map(p => p.path), ['a','b','a','b'].map(id => '/api/resources/'+id+'/deleted-licenses'));
});
test('metadata editing defaults to preserving key without fetching or submitting plaintext',async()=>{
    const h=harness();h.ui.licenseDialog(h.license);
    await h.elements.get('dialog-form').onsubmit({preventDefault(){}});
    assert.equal(h.writes.length,1);assert.equal(h.writes[0].body.secretChange,0);assert.equal(h.writes[0].body.payload,null);
});
test('vault search offers matching resources and accounts using one metadata request', async () => {
    const h = harness(), calls=[];
    h.ui.state.resources=[{id:'db',groupId:'it',name:'Database',permissions:3}];
    h.ui.state.groups=[{id:'it',name:'Infrastructure'}];
    h.context.fetch=async (path,options)=>{
        calls.push([path,JSON.parse(options.body)]);
        return {ok:true,json:async()=>({hasMore:false,items:[{resourceId:'db',resourceName:'Database',kind:'account',title:'Database reader'}]})};
    };
    h.ui.contentSearchDialog();
    const body=h.elements.get('dialog-content');
    body.querySelector('input').value='Data';
    await h.elements.get('dialog-form').onsubmit({preventDefault(){}});
    assert.equal(body.querySelector('a').href,'/resources/db');
    assert.ok(body.querySelectorAll('button').some(button=>button.textContent==='Database reader'));
    assert.deepEqual(calls,[['/api/content/search',{query:'Data'}]]);
});
test('reader landing page prioritizes vault search and exposes an active resource filter on return', () => {
    const h=harness();
    h.ui.state.resources=[{id:'r',name:'Database',permissions:3}];
    h.ui.resourcesPage();
    const content=h.elements.get('content');
    assert.equal(content.children[0].tag,'form');
    assert.equal(content.children[0].querySelector('input').type,'search');
    assert.equal(content.querySelector('details').open,false);
    assert.equal(content.querySelectorAll('button').some(x=>x.attributes['aria-label']==='Behörigheter · Database'),false);
    h.ui.state.resourceSearch='Database';
    content.replaceChildren();
    h.ui.resourcesPage();
    assert.equal(content.querySelector('details').open,true);
});
test('resource cards expose access administration only on manageable resources', () => {
    const h=harness(),cards=new Element();
    h.ui.resourceCards(cards,[{id:'r',name:'Reader',canManage:false},{id:'a',name:'Admin',canManage:true}]);
    assert.equal(cards.children[0].querySelector('a').href,'/resources/r');
    assert.equal(cards.children[0].querySelectorAll('button').length,0);
    assert.equal(cards.children[1].querySelector('button').attributes['aria-label'],'Behörigheter · Admin');
});
test('clipboard failure explains how to read the password without exposing it in feedback',async()=>{
    const h=harness();
    h.context.navigator.clipboard.writeText=async()=>{throw new Error('clipboard unavailable');};
    const copying=h.ui.copySecret({id:'example'});
    h.complete({ok:true,json:async()=>({password:'synthetic-only'})});
    await assert.rejects(copying,{message:'Kunde inte kopiera till urklipp. Välj Visa för att läsa lösenordet.'});
});

for(const [zone,expected]of [['America/New_York','2026-10-13'],['America/Los_Angeles','2026-10-13'],['Europe/Stockholm','2026-10-14'],['Asia/Tokyo','2026-10-14']])test('unchanged license expiry preserves exact instant in '+zone,async()=>{
 const previous=process.env.TZ;try{process.env.TZ=zone;const h=harness(),instant='2026-10-14T03:59:59Z';h.ui.licenseDialog({...h.license,expiresAt:instant});
 const expiry=h.elements.get('dialog-content').querySelectorAll('input').find(x=>x.type==='date');assert.equal(expiry.value,expected);
 await h.elements.get('dialog-form').onsubmit({preventDefault(){}});assert.equal(h.writes[0].body.expiresAt,instant);
 }finally{if(previous===undefined)delete process.env.TZ;else process.env.TZ=previous;}
});
test('changed license expiry uses local end-of-day across DST',async()=>{
 const previous=process.env.TZ;try{process.env.TZ='America/New_York';const h=harness();h.ui.licenseDialog({...h.license,expiresAt:'2026-10-14T03:59:59Z'});
 h.elements.get('dialog-content').querySelectorAll('input').find(x=>x.type==='date').value='2026-11-01';await h.elements.get('dialog-form').onsubmit({preventDefault(){}});
 assert.equal(h.writes[0].body.expiresAt,'2026-11-02T04:59:59.000Z');
 }finally{if(previous===undefined)delete process.env.TZ;else process.env.TZ=previous;}
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
test('clearing audit filters drops both draft criteria and the old snapshot', async () => {
    const h=harness({events:[]});
    h.ui.state.auditFilters={action:'Secret.Create', applied:{action:'Secret.Create',offset:200,snapshotId:'old'}};
    await h.ui.auditPage();
    const content=h.elements.get('content');
    const action=content.querySelectorAll('input').find(input=>input.labels.some(label=>label.children[0]?.textContent==='Operation'));
    action.value='License.Reveal';
    action.events.input();
    const clear=content.querySelectorAll('button').find(control=>control.textContent==='Rensa filter');
    await clear.events.click();
    assert.equal(action.value,'');
    assert.equal(h.ui.state.auditFilters.applied.action,'');
    assert.equal(h.ui.state.auditFilters.applied.offset,0);
    assert.equal(h.ui.state.auditFilters.applied.snapshotId,undefined);
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
test('new passwords show only relevant fields and keep typed values on submission', async () => {
    const h = harness();
    h.ui.secretDialog();
    const body = h.elements.get('dialog-content');
    assert.equal(body.querySelectorAll('select').length, 0);
    const password = body.querySelectorAll('input').find(input => input.type === 'password');
    assert.equal(password.required, true);
    password.value = 'synthetic-password';
    await h.elements.get('dialog-form').onsubmit({preventDefault() {}});
    assert.equal(h.writes[0].body.payload.password, 'synthetic-password');
    assert.equal(h.writes[0].body.ldapProfileId, null);
});
test('editing a title preserves an existing profile absent from the available list', async () => {
    const h = harness();
    h.ui.secretDialog({id:'test', title:'Example', ldapProfileId:'previous', revision:1});
    await h.elements.get('dialog-form').onsubmit({preventDefault() {}});
    assert.equal(h.writes[0].body.ldapProfileId, 'previous');
    assert.equal(h.writes[0].body.payload, null);
});

test('grant validity includes an open-ended future start and distinguishes active and expired access', () => {
    const {ui} = harness();
    const start = '2026-03-01T00:00:00Z', end = '2026-04-01T00:00:00Z';
    assert.match(ui.grantValidity({startsAt:start}, Date.parse(start)-1), /Schemalagd.*start/);
    assert.match(ui.grantValidity({startsAt:start, expiresAt:end}, Date.parse(start)), /Aktiv/);
    assert.match(ui.grantValidity({expiresAt:end}, Date.parse(end)), /Utgången/);
});
test('direct copy is available only for identified accounts on readable resources', () => {
    const {ui} = harness();
    const resource = {id:'r',permissions:1};
    ui.state.resources = [resource];
    const hit = {kind:'account',id:'s',resourceId:'r',title:'Example'};
    assert.equal(ui.accountCopy(hit), '');
    resource.permissions = 3;
    assert.equal(ui.accountCopy(hit).tag, 'button');
    assert.equal(ui.accountCopy({...hit,kind:'license'}), '');
    assert.equal(ui.accountCopy({...hit,id:null}), '');
});
