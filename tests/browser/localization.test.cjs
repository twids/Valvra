const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const root = 'src/Valvra.Web/wwwroot/';
const catalogs = Object.fromEntries(['sv','en'].map(code => [code, JSON.parse(fs.readFileSync(root+'i18n/'+code+'.json','utf8'))]));
class Element {
    constructor(text='') { this.textContent=text; this.attributes={}; this.events={}; }
    setAttribute(key,value) { this.attributes[key]=value; }
    getAttribute(key) { return this.attributes[key]??null; }
    removeAttribute(key) { delete this.attributes[key]; }
    addEventListener(key,callback) { this.events[key]=callback; }
}
async function harness({cookie='',languages=['sv-SE']}={}) {
    const elements=[],events=[];
    const document={cookie,documentElement:{lang:''},querySelectorAll:selector=>elements.filter(element=>element.getAttribute(selector.slice(1,-1))!==null),dispatchEvent:event=>events.push(event.type)};
    const context={document,window:{},navigator:{languages},location:{protocol:'https:'},CustomEvent:class{constructor(type){this.type=type;}},fetch:async path=>({ok:true,json:async()=>catalogs[path.includes('/en.')?'en':'sv']})};
    vm.runInNewContext(fs.readFileSync(root+'js/i18n.js','utf8'),context);
    await context.window.ValvraI18n.ready;
    return {i18n:context.window.ValvraI18n,document,elements,events};
}
test('catalogs cover the same keys and preserve all formatting parameters',()=>{
    assert.deepEqual(Object.keys(catalogs.sv).sort(),Object.keys(catalogs.en).sort());
    for(const [key,value] of Object.entries(catalogs.en)) {
        assert.ok(value.trim(),key);
        assert.deepEqual([...key.matchAll(/\{\w+\}/g)].map(x=>x[0]).sort(),[...value.matchAll(/\{\w+\}/g)].map(x=>x[0]).sort(),key);
    }
});
test('every explicitly localized browser label exists in both catalogs',()=>{
    for(const file of ['js/app.js','js/settings.js','js/import.js','js/setup.js','../Pages/Index.cshtml','../Pages/Setup.cshtml','../../../demo/Valvra.Demo/wwwroot/index.html']) {
        const source=fs.readFileSync(root+file,'utf8');
        for(const match of source.matchAll(/(?:\bt\(|data-i18n(?:-aria-label|-placeholder)?=)"([^"\n]*)"/g))
            assert.ok(Object.hasOwn(catalogs.en,match[1]),file+': '+match[1]);
    }
});
test('browser language fallback, unsupported values and cookie precedence are deterministic',async()=>{
    assert.equal((await harness({languages:['de-DE','en-US']})).i18n.language,'en');
    assert.equal((await harness({languages:['EN-US']})).i18n.language,'en');
    assert.equal((await harness({languages:['fr-FR']})).i18n.language,'sv');
    assert.equal((await harness({cookie:'other=1; Valvra.Language=sv',languages:['en-US']})).i18n.language,'sv');
    const h=await harness({cookie:'Valvra.Language=invalid',languages:['en-US']});
    assert.equal(h.i18n.language,'en'); assert.equal(h.i18n.setLanguage('xx'),false); assert.equal(h.i18n.language,'en');
});
test('changing language updates only marked UI text and keeps user data exact',async()=>{
    const h=await harness(); const label=new Element(),userData=new Element(),picker=new Element();
    picker.setAttribute('data-language-picker',''); h.elements.push(label,userData,picker);
    h.i18n.setText(label,h.i18n.message('Spara')); h.i18n.setText(userData,'Spara');
    h.i18n.setLanguage('en');
    assert.equal(String(label.textContent),'Save'); assert.equal(userData.textContent,'Spara');
    assert.equal(h.document.documentElement.lang,'en'); assert.equal(picker.value,'en');
    assert.match(h.document.cookie,/Valvra.Language=en; Path=\/; Max-Age=31536000; SameSite=Lax; Secure/);
    assert.deepEqual(h.events,['valvra:languagechange']);
});
test('parameters are inserted as plain text and UI markers are removed for raw content',async()=>{
    const h=await harness({languages:['en-GB']}); const element=new Element();h.elements.push(element);
    h.i18n.setText(element,h.i18n.message('Ta bort {title}? Historiska versioner bevaras krypterade.',{title:'<img src=x onerror=alert(1)>'}));
    assert.equal(String(element.textContent),'Delete <img src=x onerror=alert(1)>? Historical versions are retained encrypted.');
    h.i18n.setText(element,'Spara'); h.i18n.setLanguage('sv'); assert.equal(element.textContent,'Spara');
    assert.equal(element.getAttribute('data-i18n'),null); assert.equal(element.getAttribute('data-i18n-values'),null);
});
test('native validation feedback uses the selected language',async()=>{
    const h=await harness({languages:['en-US']});
    assert.equal(h.i18n.validation({validity:{valueMissing:true}}),'Complete the required field.');
    h.i18n.setLanguage('sv'); assert.equal(h.i18n.validation({validity:{valueMissing:true}}),'Fyll i det obligatoriska fältet.');
});
test('missing catalog keys fall back to the source without reading object prototypes',async()=>{
    const h=await harness({languages:['en-US']});
    for(const key of ['Unknown label','constructor','__proto__','toString']) assert.equal(String(h.i18n.message(key)),key);
});
