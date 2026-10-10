const {defineConfig} = require('@playwright/test');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
module.exports = defineConfig({
    testDir: '../accessibility', testMatch: 'accessibility.spec.cjs', grep: /installation guide:/,
    workers: 1, timeout: 30000, retries: 0, reporter: [['list']],
    outputDir: path.join(root, 'artifacts/shared-audit-browser-results'),
    use: {baseURL: 'http://localhost:58918', locale: 'sv-SE', viewport: {width: 1280, height: 900}, trace: 'retain-on-failure'},
    webServer: {
        command: 'dotnet artifacts/shared-audit-preview/Valvra.Preview.dll', cwd: root,
        url: 'http://localhost:58918/api/session', env: {VALVRA_BROWSER_TEST_PORT: '58918'},
        reuseExistingServer: false, timeout: 60000
    }
});
