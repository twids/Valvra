const {test} = require('node:test');
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const {once} = require('node:events');
const {stopProcess} = require('./stop-process.cjs');

test('cleanup terminates and reaps a process that ignores SIGTERM', {timeout: 10000}, async () => {
    const child = spawn(process.execPath, ['-e', "process.on('SIGTERM',()=>{});setInterval(()=>{},1000);console.log('ready');"], {stdio: ['ignore', 'pipe', 'pipe']});
    try {
        await once(child.stdout, 'data');
        await stopProcess(child, 100);
        assert.ok(child.exitCode !== null || child.signalCode !== null);
        if (process.platform !== 'win32') assert.equal(child.signalCode, 'SIGKILL');
        await stopProcess(child, 100);
    } finally {
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    }
});
