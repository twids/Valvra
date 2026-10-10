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
test('execution reads only metadata and skips duplicate titles without revealing or overwriting',async()=>{
    const preview=plan(mapped('title,password\nExisting,pw\nNew,pw\nnew,pw'),[],[{id:'r',name:'R',permissions:5}],null,'r');
    const calls=[];
    const report=await execute(preview,async(path,body)=>{calls.push([path,body]);return body?{id:'new'}:[{title:'existing'}];},()=>false,()=>{});
    assert.equal(report.created,1); assert.equal(report.skipped,2); assert.equal(report.stopped,false);
    assert.deepEqual(calls.map(x=>x[0]),['/resources/r/secrets','/resources/r/secrets']);
    assert.equal(preview.entries[1].payload.password,'');
});
test('execution reports an uncertain row on a failed write and never retries it',async()=>{
    const preview=plan(mapped('title,password\nA,pw\nB,pw'),[],[{id:'r',name:'R',permissions:5}],null,'r');
    let writes=0;
    const report=await execute(preview,async(path,body)=>{if(!body)return [];if(++writes===2)throw Error('secret must not be echoed');return {id:'a'};},()=>false,()=>{});
    assert.equal(report.created,1); assert.equal(report.uncertainRow,3); assert.equal(report.stopped,true);
    assert.equal(JSON.stringify(report).includes('secret'),false); assert.equal(writes,2);
});
test('cancellation stops between requests and hierarchy writes use resolved IDs',async()=>{
    const preview=plan(mapped('title,password,group\nA,pw,One/Two'),[group],[],'g',null);
    const calls=[]; let stopped=false;
    const report=await execute(preview,async(path,body)=>{calls.push([path,body]);if(path==='/resources')stopped=true;return {id:'id'+calls.length};},()=>stopped,()=>{});
    assert.equal(report.created,0); assert.equal(report.groups,2); assert.equal(report.resources,1); assert.equal(report.stopped,true);
    assert.equal(calls[1][1].parentId,'id1'); assert.equal(calls[2][1].groupId,'id2');
});
