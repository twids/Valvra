const {defineConfig} = require('@playwright/test');
module.exports = defineConfig({
    testDir: './tests/accessibility',
    fullyParallel: false,
    workers: 1,
    timeout: 30000,
    retries: 0,
    reporter: [['list'], ['html', {outputFolder: 'artifacts/accessibility-report', open: 'never'}]],
    outputDir: 'artifacts/accessibility-results',
    use: {baseURL: 'http://localhost:58902', locale: 'sv-SE', viewport: {width: 1280, height: 900}, trace: 'retain-on-failure'},
    webServer: [{
        command: 'dotnet run --project tests/Valvra.Preview -c Release --no-build --no-launch-profile',
        url: 'http://localhost:58902/api/session',
        env: {VALVRA_BROWSER_TEST_PORT: '58902'},
        reuseExistingServer: false,
        timeout: 60000
    }, {
        command: 'dotnet run --project demo/Valvra.Demo -c Release --no-build --no-launch-profile --urls http://127.0.0.1:58903',
        url: 'http://127.0.0.1:58903/api/session',
        reuseExistingServer: false,
        timeout: 60000
    }]
});
