const {chromium} = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const output = path.resolve(process.argv[2]);
const url = fs.readFileSync(path.join(output, 'management-url.txt'), 'utf8');
const base = url.split('#')[0].replace(/\/$/, '');
const token = url.split('#')[1];
const results = [];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  const api = (route, body, headers = {}) => page.request.fetch(base + route, {
    method: body === undefined ? 'GET' : 'POST', data: body,
    headers: {Authorization: 'Bearer ' + token, Origin: base, ...headers}
  });
  async function test(id, name, run) {
    try { await run(); results.push({id, name, status: 'PASS'}); }
    catch (e) {
      results.push({id, name, status: 'FAIL', error: e.message});
      await page.screenshot({path: path.join(output, id + '-failure.png'), fullPage: true, animations: 'disabled'});
    }
    fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify(results, null, 2));
  }
  async function status() { await page.waitForFunction(() => document.querySelector('#status').textContent.includes('已保存')); }
  await page.goto(url);
  await page.waitForFunction(() => document.querySelector('#appVersion').textContent === 'v0.5.3');
  await test('B01', 'Version, author and four management tabs', async () => {
    assert.match(await page.locator('#appAuthor').innerText(), /Kaeless/);
    for (const section of ['settings', 'toolbar', 'history', 'exclusions']) {
      await page.locator('#' + section + 'Tab').click();
      assert.equal(await page.locator('#' + section + 'Panel').isVisible(), true);
      if (section === 'history') await page.waitForFunction(() => document.querySelector('#historyIndexCount').textContent.includes('条'));
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({path: path.join(output, 'management-' + section + '.png'), fullPage: true, animations: 'disabled'});
    }
  });
  await test('B02', 'Save settings through UI and verify backend persistence', async () => {
    await page.locator('#settingsTab').click(); await page.locator('#language').fill('English');
    await page.locator('#status').evaluate(e => e.textContent = '');
    await page.locator('#saveSettings').click(); await status();
    assert.equal((await (await api('/api/settings')).json()).TargetLanguage, 'English');
  });
  await test('B03', 'Create and delete API profile; reject deleting the only profile', async () => {
    const initial = await (await api('/api/settings')).json();
    assert.equal((await api('/api/connection/delete', {id: initial.ActiveApiId})).status(), 400);
    await page.locator('#newConnection').click(); await page.locator('#apiName').fill('CI second API');
    await page.locator('#apiBaseUrl').fill('http://127.0.0.1:12346'); await page.locator('#model').fill('fixture-2');
    await page.locator('#saveConnection').click();
    await page.waitForFunction(() => document.querySelector('#status').textContent.includes('API 配置已保存'));
    const saved = await (await api('/api/settings')).json();
    const profile = saved.ApiProfiles.find(p => p.Name === 'CI second API');
    assert.ok(profile); assert.equal(saved.ActiveApiId, profile.Id);
    assert.equal(JSON.stringify(saved).includes('TEST-ONLY-NOT-A-REAL-KEY'), false);
    assert.equal((await api('/api/connection/select', {id: initial.ActiveApiId})).status(), 200);
    assert.equal((await api('/api/connection/delete', {id: profile.Id})).status(), 200);
  });
  await test('B04', 'Custom toolbar button persists', async () => {
    await page.locator('#toolbarTab').click(); await page.locator('#addCustomAction').click();
    await page.locator('#customActionList input').last().fill('总结');
    await page.locator('#customActionList textarea').last().fill('总结所选内容');
    await page.locator('#status').evaluate(e => e.textContent = '');
    await page.locator('#saveToolbar').click(); await status();
    assert.ok((await (await api('/api/settings')).json()).CustomActions.some(a => a.Name === '总结' && a.Id));
    await page.screenshot({path: path.join(output, 'management-custom-toolbar.png'), fullPage: true, animations: 'disabled'});
  });
  await test('B05', 'History Markdown, search, index, literal HTML safety and deletion', async () => {
    await page.locator('#historyTab').click();
    await page.waitForFunction(() => document.querySelector('#historyList').textContent.includes('TCP'));
    assert.ok(await page.locator('#historyIndex button').count());
    assert.equal(await page.evaluate(() => window.xss), undefined);
    assert.ok((await page.locator('#historyList').innerText()).includes('<script>'));
    assert.ok((await page.locator('#historyList .result h1').allTextContents()).includes('标题'));
    assert.equal(await page.locator('#historyList .result strong').last().innerText(), '测试内容');
    assert.ok(await page.locator('#historyList .result ul').count());
    assert.equal(await page.locator('#historyList .result ul').first().evaluate(el => getComputedStyle(el).listStyleType), 'disc');
    assert.ok(await page.locator('#historyList .result pre code').count());
    assert.equal(await page.locator('#historyList .result script').count(), 0);
    await page.locator('#historySearch').fill('no-matching-history-xyz');
    await page.waitForFunction(() => document.querySelector('#historyIndexCount').textContent === '');
    await page.locator('#historySearch').fill('TCP');
    await page.waitForFunction(() => document.querySelector('#historyIndexCount').textContent.includes('条'));
    const entries = await (await api('/api/history?search=TCP')).json();
    assert.ok(entries.length); assert.equal((await api('/api/history/delete', {id: entries[0].Id})).status(), 200);
    assert.equal((await (await api('/api/history?search=TCP')).json()).length, entries.length - 1);
  });
  await test('B06', 'Add and remove excluded program through UI', async () => {
    await page.locator('#exclusionsTab').click();
    await page.locator('#exeFile').setInputFiles({name: 'blocked.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('fixture')});
    await page.waitForFunction(() => document.querySelector('#excludedList').textContent.includes('blocked.exe'));
    await page.locator('#excludedList li').filter({hasText: 'blocked.exe'}).getByRole('button', {name: '移除'}).click();
    await page.waitForFunction(() => !document.querySelector('#excludedList').textContent.includes('blocked.exe'));
  });
  await test('B07', 'Unauthorized token, wrong Host and cross-origin are rejected', async () => {
    assert.equal((await api('/api/settings', undefined, {Authorization: ''})).status(), 401);
    assert.equal((await api('/api/settings', undefined, {Host: 'evil.example'})).status(), 403);
    assert.equal((await api('/api/settings', undefined, {Origin: 'https://evil.example'})).status(), 403);
  });
  await test('B08', 'Insecure remote endpoint and negative history offset are rejected', async () => {
    assert.equal((await api('/api/connection', {Name: 'invalid', Model: 'fixture', ApiBaseUrl: 'http://remote.example', ApiKey: ''})).status(), 400);
    assert.equal((await api('/api/history?offset=-1')).status(), 400);
  });
  await test('B09', 'Responsive settings at 900px and no browser script errors', async () => {
    await page.setViewportSize({width: 900, height: 800}); await page.locator('#settingsTab').click();
    assert.equal(await page.locator('#settingsPanel').isVisible(), true); assert.deepEqual(errors, []);
    await page.screenshot({path: path.join(output, 'management-900px.png'), fullPage: true, animations: 'disabled'});
  });
  await browser.close();
  if (results.some(r => r.status === 'FAIL')) process.exitCode = 1;
})().catch(e => { fs.writeFileSync(path.join(output, 'browser-error.txt'), e.stack); process.exitCode = 1; })
  .finally(() => fs.writeFileSync(path.join(output, 'browser-done'), 'done'));
