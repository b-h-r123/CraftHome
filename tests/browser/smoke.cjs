// Optional end-to-end check against an isolated Linux host. Credentials stay in a private file.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const base = process.env.CRAFTHOME_URL || 'http://127.0.0.1:5080';
const reportDirectory = process.env.CRAFTHOME_REPORTS || 'reports';
const loginFile = process.env.CRAFTHOME_LOGIN_FILE;
if (!loginFile) throw new Error('Set CRAFTHOME_LOGIN_FILE to a private first-login.txt or a one-line password file.');
const text = fs.readFileSync(loginFile, 'utf8').replace(/^\uFEFF/, '');
const password = text.match(/^密码：(.*)$/m)?.[1] || text.trim();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
fs.mkdirSync(reportDirectory, { recursive: true });
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.CRAFTHOME_BROWSER ? { executablePath: process.env.CRAFTHOME_BROWSER } : {}), args: ['--no-sandbox', '--no-proxy-server'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [], checks = [];
  let stage = 'login';
  page.on('pageerror', error => errors.push(error.name));
  page.on('console', message => { if (message.type() === 'error') errors.push('browser console error'); });
  try {
    await page.goto(base);
    await page.getByLabel('邮箱', { exact: true }).fill('owner@crafthome.local');
    await page.getByLabel('密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '登录控制台' }).click();
    await page.getByRole('heading', { name: '服务器', exact: true }).waitFor();
    checks.push('owner login');
    console.log('Wizard smoke: owner login passed');
    await page.getByRole('link', { name: '新手开服', exact: true }).click();
    stage = 'load wizard';
    await page.locator('#create-name').waitFor({ timeout: 60000 });
    await page.waitForFunction(() => !document.querySelector('#create-name')?.disabled && !document.querySelector('#create-name')?.closest('fieldset')?.disabled, null, { timeout: 60000 });
    checks.push('environment and official versions loaded');
    console.log('Wizard smoke: environment and versions loaded');
    await page.locator('#create-name').fill('CraftHome wizard smoke');
    await page.locator('#create-version').selectOption(process.env.CRAFTHOME_GAME_VERSION || '1.21.11');
    await page.locator('#create-host').fill(new URL(base).hostname);
    if (process.env.CRAFTHOME_GAME_PORT) await page.locator('#create-port').fill(process.env.CRAFTHOME_GAME_PORT);
    await page.locator('#create-memory').fill(process.env.CRAFTHOME_GAME_MEMORY || '512');
    if (process.env.CRAFTHOME_OFFLINE_PLAYER) {
      await page.locator('#create-offline').check();
      await page.locator('#create-player').fill(process.env.CRAFTHOME_OFFLINE_PLAYER);
    }
    await page.getByRole('button', { name: '创建服务器', exact: true }).click();
    stage = 'EULA gate';
    await page.getByText('请先阅读并确认 Minecraft EULA。', { exact: true }).waitFor();
    checks.push('EULA gate');
    await page.locator('#create-eula').check();
    await page.getByRole('button', { name: '创建服务器', exact: true }).click();
    stage = 'create';
    await page.getByText('请先阅读并确认 Minecraft EULA。', { exact: true }).waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('.notice-error') || [...document.querySelectorAll('button')].some(button => button.textContent.trim() === '首次启动并管理'), null, { timeout: 300000 });
    if (await page.locator('.notice-error').count()) throw new Error('Creation error');
    checks.push('verified download, independent instance and configuration');
    console.log('Wizard smoke: server creation passed');
    await page.screenshot({ path: path.join(reportDirectory, 'wizard-created.png'), fullPage: true });
    stage = 'first start click';
    await page.getByRole('button', { name: '首次启动并管理', exact: true }).click();
    console.log('Wizard smoke: first start submitted');
    stage = 'first start response';
    await page.waitForFunction(() => document.querySelector('.notice-error') || document.querySelector('h1')?.textContent.trim() === '服务器', null, { timeout: 300000 });
    if (await page.locator('.notice-error').count()) throw new Error('Startup error');
    await page.getByText('查询已响应', { exact: true }).waitFor({ timeout: 30000 });
    checks.push('first startup and game status response');
    await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === '正常停止' && !button.disabled));
    await page.screenshot({ path: path.join(reportDirectory, 'wizard-running-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Mobile overflow');
    await page.screenshot({ path: path.join(reportDirectory, 'wizard-running-mobile.png'), fullPage: true });
    checks.push('mobile layout');
    await page.getByRole('button', { name: '正常停止', exact: true }).click();
    await page.getByRole('button', { name: '确认停止', exact: true }).click();
    for (let attempt = 0; attempt < 30; attempt++) {
      await sleep(1500); await page.reload();
      if (await page.getByText('已停止', { exact: true }).isVisible()) break;
    }
    await page.getByText('已停止', { exact: true }).waitFor();
    checks.push('normal stop');
    if (errors.length) throw new Error('Browser runtime errors');
    const result = { date: new Date().toISOString(), url: base, checks, errors, browserPlugin: 'not available; Playwright used' };
    fs.writeFileSync(path.join(reportDirectory, 'wizard-smoke.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result));
  } catch (error) {
    await page.screenshot({ path: path.join(reportDirectory, 'wizard-failure.png'), fullPage: true }).catch(() => {});
    // Locator diagnostics may contain form input values; do not print raw exceptions or traces.
    console.error('Wizard smoke failed at ' + stage + ' (' + error.name + '). Inspect the screenshot and server logs privately.');
    process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error('Browser setup failed (' + error.name + ').'); process.exitCode = 1; });
