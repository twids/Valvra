const {spawnSync} = require('node:child_process');
const path = require('node:path');
const root = path.resolve(__dirname,'../..');
const build = spawnSync('dotnet',['build','tests/Valvra.Preview','-c','Release','-m:1','-p:OutDir='+path.join(root,'artifacts','import-preview')+path.sep],{cwd:root,stdio:'inherit'});
if(build.error) { console.error(build.error.message); process.exit(1); }
if(build.status!==0) process.exit(build.status||1);
const tests = spawnSync(process.execPath,[require.resolve('@playwright/test/cli'),'test','--config','tests/browser/import-playwright.config.cjs'],{cwd:root,stdio:'inherit'});
if(tests.error) { console.error(tests.error.message); process.exit(1); }
process.exit(tests.status ?? 1);
