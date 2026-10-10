const {defineConfig} = require('@playwright/test');
const baseURL = process.env.VALVRA_IMPORT_TEST_URL;
if (!baseURL) throw new Error('Run npm run test:import to start an isolated preview on an OS-assigned port.');
module.exports = defineConfig({
    testDir: '../import', workers: 1, fullyParallel: false, timeout: 30000, retries: 0,
    reporter: 'list', outputDir: '../../artifacts/import-browser-results',
    // run-import owns this fresh host and waits until its fixture data is seeded.
    use: {baseURL, locale: 'sv-SE', viewport: {width:1280,height:900}, trace: 'retain-on-failure'}
});
