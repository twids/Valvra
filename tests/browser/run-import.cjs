const {spawn, spawnSync} = require('node:child_process');
const path = require('node:path');
const root = path.resolve(__dirname,'../..');
const build = spawnSync('dotnet',['build','tests/Valvra.Preview','-c','Release','-m:1','-p:OutDir='+path.join(root,'artifacts','import-preview')+path.sep],{cwd:root,stdio:'inherit'});
if(build.error) { console.error(build.error.message); process.exit(1); }
if(build.status!==0) process.exit(build.status||1);
async function main() {
    // The server binds port zero itself, avoiding fixed-port collisions and
    // the race between probing a free port and actually binding to it.
    const host = spawn('dotnet', ['artifacts/import-preview/Valvra.Preview.dll'], {
        cwd: root, env: {...process.env, VALVRA_BROWSER_TEST_PORT: '0'},
        stdio: ['ignore', 'pipe', 'inherit']
    });
    let tests;
    const stop = () => { tests?.kill(); host.kill(); };
    process.once('SIGINT', stop);
    process.once('SIGTERM', stop);
    try {
        const url = await new Promise((resolve, reject) => {
            let output = '';
            let ready = false;
            const timer = setTimeout(() => finish(new Error('Import preview did not become ready within 60 seconds.')), 60000);
            const onError = error => finish(error);
            const onExit = (code, signal) => finish(new Error(`Import preview exited before readiness (${signal || code}).`));
            function finish(error, address) {
                if (ready) return;
                ready = true;
                clearTimeout(timer);
                host.removeListener('error', onError);
                host.removeListener('exit', onExit);
                error ? reject(error) : resolve(address);
            }
            host.once('error', onError);
            host.once('exit', onExit);
            host.stdout.on('data', chunk => {
                process.stdout.write(chunk);
                if (ready) return;
                output += chunk.toString();
                const match = output.match(/^BROWSER_TEST_URL=(http:\/\/localhost:\d+)\r?$/m);
                if (match) finish(null, match[1]);
            });
        });
        tests = spawn(process.execPath, [require.resolve('@playwright/test/cli'), 'test', '--config', 'tests/browser/import-playwright.config.cjs'], {
            cwd: root, stdio: 'inherit', env: {...process.env, VALVRA_IMPORT_TEST_URL: url}
        });
        return await new Promise((resolve, reject) => {
            tests.once('error', reject);
            tests.once('exit', code => resolve(code ?? 1));
            host.once('exit', () => { if (tests.exitCode === null) tests.kill(); });
            if (host.exitCode !== null || host.signalCode !== null) tests.kill();
        });
    } finally {
        stop();
        process.removeListener('SIGINT', stop);
        process.removeListener('SIGTERM', stop);
    }
}
main().then(code => { process.exitCode = code; }, error => { console.error(error); process.exitCode = 1; });
