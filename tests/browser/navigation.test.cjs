const {test} = require('node:test');
const assert = require('node:assert/strict');
const routes = require('../../src/Valvra.Web/wwwroot/js/navigation.js');

test('all views and group/resource contexts round-trip using stable identifiers',()=>{
    for(const route of [{view:'vault'},{view:'groups'},{view:'licenses'},{view:'audit'},{view:'settings'},
        {view:'vault',resourceId:'abc-123'},{view:'groups',groupId:'def-456'},
        {view:'groups',groupId:'def-456',resourceId:'abc-123'}])
        assert.deepEqual(routes.parse(routes.url(route)),route);
    assert.deepEqual(routes.parse('/'),{view:'vault'});
    assert.deepEqual(routes.parse('/groups/DEF-456/'),{view:'groups',groupId:'def-456'});
});
test('unknown routes, encoded delimiters, query data and excess path segments are rejected',()=>{
    for(const path of ['/api/session','/setup','/resources/a/extra','/groups/a/licenses/b',
        '/groups/a/resources','//evil.example','/resources/../audit','/resources/%2f','/resources/a?password=value',
        '/resources/'+ 'x'.repeat(129)]) assert.equal(routes.parse(path),null,path);
});
test('unsafe resource identifiers and unsupported resource contexts cannot produce links',()=>{
    for(const route of [{view:'vault',resourceId:'<script>'},{view:'vault',resourceId:'a/b'},
        {view:'licenses',resourceId:'abc'},{view:'groups',resourceId:'abc'},{view:'invalid'}])
        assert.throws(()=>routes.url(route));
});
