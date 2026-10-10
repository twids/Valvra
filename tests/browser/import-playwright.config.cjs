const {defineConfig} = require('@playwright/test');
const path = require('node:path');
module.exports = defineConfig({
    testDir: '../import', workers: 1, fullyParallel: false, timeout: 30000, retries: 0,
    reporter: 'list', outputDir: '../../artifacts/import-browser-results',
    use: {baseURL: 'http://localhost:58912', locale: 'sv-SE', viewport: {width:1280,height:900}, trace: 'retain-on-failure'},
    // A separate host keeps imported records out of other suites' fixed fixture data.
    webServer: {command: 'dotnet artifacts/import-preview/Valvra.Preview.dll', cwd: path.resolve(__dirname,'../..'),
        url: 'http://localhost:58912/api/session', env: {VALVRA_BROWSER_TEST_PORT:'58912'}, reuseExistingServer: false, timeout:60000}
});
