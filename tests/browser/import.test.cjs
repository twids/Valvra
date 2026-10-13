const {test} = require('node:test');
const assert = require('node:assert/strict');
const {parseCsv, suggest, mapRows, plan, execute} = require('../../src/Valvra.Web/wwwroot/js/import.js');
const group = {id:'g', parentId:null, name:'IT', canManage:true, permissions:13};
const csv = text => parseCsv(text);
const mapped = text => { const data=csv(text); return mapRows(data,suggest(data.headers),'Konton'); };
test('CSV handles BOM, semicolon, escaped quotes, CRLF and multiline notes without trimming passwords',()=>{
    const data=csv('\uFEFFTitel;Lösenord;Anteckningar;Grupp\r\n"A;B";" secret ";"one\r\ntwo ""quotes""";IT/Servrar\r\n');
    const rows=mapRows(data,suggest(data.headers),'Konton');
    assert.equal(data.delimiter,';'); assert.equal(rows[0].title,'A;B');
    assert.equal(rows[0].payload.password,' secret '); assert.equal(rows[0].payload.notes,'one\r\ntwo "quotes"');
    assert.deepEqual(rows[0].path,['IT','Servrar']);
    assert.equal(csv('title\tpassword\nA\tB').delimiter,'\t');
});
test('reject malformed input, duplicate headers, excess rows, bytes, fields and depth',()=>{
    for(const text of ['title,password\n"broken,secret','title,title\nA,B','title,password\nA,B,C','title,password\nA,\0']) assert.throws(()=>csv(text));
    assert.throws(()=>csv('title,password\n'+Array(1001).fill('A,B').join('\n')));
    assert.throws(()=>csv('a'.repeat(2*1024*1024+1)));
    assert.throws(()=>csv('title,password\nA,'+'b'.repeat(65537)));
    assert.throws(()=>mapped('title,password,group\nA,B,A/B/C'));
    assert.throws(()=>mapped('title,password,group\nA,B,../IT'));
});
test('explicit mapping supports two separate group columns and preserves URLs as notes',()=>{
    const rows=mapped('title,password,group,subgroup,url\nRoot,pw,IT,Servers,https://example.test');
    assert.deepEqual(rows[0].path,['IT','Servers']); assert.equal(rows[0].payload.notes,'URL: https://example.test');
    const data=csv('a,b\nA,B'); assert.throws(()=>mapRows(data,{title:0,password:0},'R'));
});
test('planner reuses siblings and resources, creates shared hierarchy once, and supports manual remapping',()=>{
    const rows=mapped('title,password,group,resource\nA,pw,IT/Servrar,SQL\nB,pw,IT/Servrar,SQL');
    const base={...group,name:'Bas'};
    const existing={id:'it',parentId:'g',name:'IT',canManage:true,permissions:13};
    const result=plan(rows,[base,existing],[],'g',null);
    assert.equal(result.groups.length,1); assert.equal(result.groups[0].parentId,'it');
    assert.equal(result.resources.length,1); assert.equal(result.entries[0].resourceId,result.entries[1].resourceId);
    const remapped=plan(rows,[base],[{id:'r',groupId:'g',name:'SQL',permissions:5}],null,null,{'["IT","Servrar"]':'g'});
    assert.equal(remapped.groups.length,0); assert.equal(remapped.resources.length,0); assert.equal(remapped.entries[0].resourceId,'r');
});
test('admin management alone cannot import; writable existing resource needs no group administration',()=>{
    const rows=mapped('title,password\nA,pw');
    assert.throws(()=>plan(rows,[{...group,permissions:0}],[],'g',null));
    assert.throws(()=>plan(rows,[group],[{id:'r',name:'R',permissions:1}],'g','r'));
    assert.equal(plan(rows,[],[{id:'r',name:'R',permissions:5}],null,'r').entries.length,1);
    assert.throws(()=>plan(mapped('title,password,group\nA,pw,Konton'),[group,{...group,id:'g2',parentId:'g',name:'Konton'},{...group,id:'g3',parentId:'g',name:'Konton'}],[],'g',null,{}));
});
test('ambiguous sibling resources fail before writes',()=>{
    assert.throws(()=>plan(mapped('title,password\nA,pw'),[group],[{id:'a',groupId:'g',name:'Konton',permissions:5},{id:'b',groupId:'g',name:'konton',permissions:5}],'g',null));
});
test('execution uses administrator batches and honors server duplicate results without revealing values',async()=>{
    const preview=plan(mapped('title,password\nExisting,pw\nNew,pw\nnew,pw'),[],[{id:'r',name:'R',permissions:5}],null,'r');
    const calls=[];
    const report=await execute(preview,async(path,body)=>{calls.push(path);return {results:body.operations.map((x,i)=>({key:x.key,id:'new',created:i===1}))};},()=>false,()=>{});
    assert.equal(report.created,1); assert.equal(report.skipped,2); assert.equal(report.stopped,false);
    assert.deepEqual(calls,['/imports/batch']);
    assert.equal(preview.entries[1].payload.password,'');
});
test('execution reports an uncertain row on a failed write and never retries it',async()=>{
    const preview=plan(mapped('title,password\nA,pw\nB,pw'),[],[{id:'r',name:'R',permissions:5}],null,'r');
    let writes=0;
    const report=await execute(preview,async(path,body)=>{writes++;return {results:[{key:body.operations[0].key,id:'a',created:true}],errorCode:'Unavailable',failedKey:body.operations[1].key,uncertainRow:3};},()=>false,()=>{});
    assert.equal(report.created,1); assert.equal(report.uncertainRow,3); assert.equal(report.stopped,true);
    assert.equal(JSON.stringify(report).includes('secret'),false); assert.equal(writes,1);
});
test('cancellation stops between bounded batches and hierarchy references remain explicit',async()=>{
    const rows = 'title,password,group\n'+Array.from({length:20},(_,i)=>`A${i},pw,One/Two`).join('\n');
    const preview=plan(mapped(rows),[group],[],'g',null);
    const calls=[]; let stopped=false;
    const report=await execute(preview,async(path,body)=>{calls.push(body.operations);stopped=true;return {results:body.operations.map(x=>({key:x.key,id:'id-'+x.key,created:true}))};},()=>stopped,()=>{});
    assert.equal(report.created,9); assert.equal(report.groups,2); assert.equal(report.resources,1); assert.equal(report.stopped,true);
    assert.equal(calls.length,1); assert.equal(calls[0].length,12);
    assert.equal(calls[0][1].target,'group:0'); assert.equal(calls[0][2].target,'group:1');
});

test('transport failure is uncertain and source error text is never returned or retried',async()=>{
    const preview=plan(mapped('title,password\nA,pw'),[],[{id:'r',name:'R',permissions:5}],null,'r');
    let calls=0;
    const report=await execute(preview,async()=>{calls++;throw Error('PRIVATE-SOURCE-VALUE');},()=>false,()=>{});
    assert.equal(calls,1); assert.equal(report.errorCode,'Transport'); assert.equal(report.uncertainRow,-1);
    assert.equal(JSON.stringify(report).includes('PRIVATE-SOURCE-VALUE'),false);
});

test('1000 entries use bounded batches rather than one request per password',async()=>{
    const preview=plan(mapped('title,password\n'+Array.from({length:1000},(_,i)=>`Entry${i},pw`).join('\n')),[],[{id:'r',name:'R',permissions:5}],null,'r');
    let calls=0;
    const report=await execute(preview,async(path,body)=>{calls++;assert.ok(body.operations.length<=12);return {results:body.operations.map(x=>({key:x.key,id:'id',created:true}))};},()=>false,()=>{});
    assert.equal(calls,84); assert.equal(report.created,1000); assert.equal(report.stopped,false);
});

test('only pre-processing rate-limit rejection is retried',async()=>{
    const preview=plan(mapped('title,password\nA,pw'),[],[{id:'r',name:'R',permissions:5}],null,'r');
    let calls=0;
    const report=await execute(preview,async(path,body)=>{if(++calls===1){const e=Error('limit');e.status=429;e.retryAfter=.001;throw e;}return {results:body.operations.map(x=>({key:x.key,id:'id',created:true}))};},()=>false,()=>{});
    assert.equal(calls,2); assert.equal(report.created,1); assert.equal(report.stopped,false);
});

test('JSON escaping cannot exceed the bounded request size',async()=>{
    const entries=Array.from({length:12},(_,i)=>({row:i+2,title:'A'+i,resourceId:'r',payload:{username:'u',password:'\\'.repeat(65536),notes:'\\'.repeat(65536)}}));
    let calls=0;
    const report=await execute({groups:[],resources:[],entries},async(path,body)=>{calls++;assert.ok(new TextEncoder().encode(JSON.stringify(body)).length<=1536*1024);return {results:body.operations.map(x=>({key:x.key,id:'id',created:true}))};},()=>false,()=>{});
    assert.ok(calls>1); assert.equal(report.created,12);
});
