const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');

(async () => {
  const root = path.resolve(__dirname, '..');
  const out = path.join(root, 'artifacts', 'web-browser-check');
  fs.mkdirSync(out, { recursive: true });
  const dotnet = process.env.TEST_DOTNET || 'dotnet';
  const dll = process.env.TEST_WEB_DLL || path.join(root, 'SysBot.Pokemon.Web/bin/Release/net10.0/SysBot.Web.dll');
  const port = process.env.TEST_WEB_PORT || '5218';
  const url = `http://127.0.0.1:${port}`;
  let log = '', browser, server;
  const errors = [], checks = [];
  try {
    server = spawn(process.env.TEST_WEB_EXE || dotnet, process.env.TEST_WEB_EXE ? ['--no-browser'] : [dll, '--no-browser'], {
      cwd: root,
      env: { ...process.env, SYSBOT_WEB_PORT: port, SYSBOT_WEB_DATA: path.join(out, 'data') },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Server startup timed out: ' + log)), 30000);
      server.on('error', reject);
      server.on('exit', code => { clearTimeout(timer); reject(new Error('Server exited: ' + code + '\n' + log)); });
      server.stdout.on('data', data => { log += data; if (log.includes('Now listening on:')) { clearTimeout(timer); resolve(); } });
      server.stderr.on('data', data => { log += data; });
    });
    async function api(route, method = 'GET', data, withHeader = true) {
      const response = await fetch(url + route, { method, headers: withHeader ? { 'Content-Type':'application/json', 'X-SysBot-Request':'local-web' } : {}, ...(data ? {body: JSON.stringify(data)} : {}) });
      return { status: response.status, body: await response.json() };
    }
    const catalog = (await api('/api/catalog')).body;
    assert(catalog.pokemon.length > 100);
    assert.equal(catalog.defaultSpecies, 'Metagross');
    assert(catalog.pokemon.find(p => p.id === 'Metagross').name.includes('巨金怪'));
    const usb = (await api('/api/usb')).body;
    assert.deepEqual(usb.ports, [], 'Browser acceptance must run without a connected Switch.');
    checks.push('real catalog and no-device USB scan; no native finalizer crash');

    browser = await chromium.launch({ headless: true, ...(process.env.TEST_BROWSER_CHANNEL ? {channel:process.env.TEST_BROWSER_CHANNEL} : {}) });
    const page = await browser.newPage({ viewport: { width: 1180, height: 900 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(url);
    await page.waitForFunction(() => !document.getElementById('send').disabled);
    assert(await page.locator('#shiny').isChecked(), 'Default Metagross should retain the selected shiny preset.');
    async function tabTo(selector) {
      for (let i = 0; i < 30; i++) {
        if (await page.evaluate(s => document.activeElement.matches(s), selector)) return;
        await page.keyboard.press('Tab');
      }
      throw new Error('Keyboard could not reach '+selector);
    }
    await tabTo('#tab-quick');
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.locator('#tab-team').getAttribute('aria-selected'),'true');
    await page.keyboard.press('ArrowLeft');
    await tabTo('#species');
    await page.keyboard.press('Control+A');
    await page.keyboard.insertText('巨金怪');
    await tabTo('#shiny');
    await page.keyboard.press('Space');
    assert(!await page.locator('#shiny').isChecked());
    await page.keyboard.press('Space');
    await page.screenshot({path:path.join(out,'quick.png'),fullPage:true});
    await tabTo('#send');
    const quickResponse = page.waitForResponse(r => r.url().endsWith('/api/orders') && r.request().method() === 'POST', {timeout:180000});
    await page.keyboard.press('Enter');
    const first = await quickResponse;
    const firstBody = await first.json();
    assert.equal(first.status(), 200, JSON.stringify(firstBody));
    assert.equal(firstBody.count, 1);
    await page.waitForFunction(() => document.querySelectorAll('#orders .order').length === 1);
    assert((await page.locator('#orders').innerText()).includes('巨金怪'));
    assert((await page.locator('#orders').innerText()).includes('待派送'));
    assert(!(await page.locator('#orders').innerText()).includes('已完成'));
    checks.push('default one-click shiny Metagross: actual ALM generation into actual SysBot queue');

    await tabTo('#orders button');
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.querySelector('#orders').textContent.includes('已取消'));
    checks.push('keyboard-only tabs, Chinese species, shiny toggle, submit and cancellation; no dialog');

    await page.locator('#tab-team').click();
    await page.locator('#example').click();
    const teamResponse = page.waitForResponse(r => r.url().endsWith('/api/orders') && r.request().method() === 'POST', {timeout:180000});
    await page.locator('#send').click();
    const team = await teamResponse, teamBody = await team.json();
    assert.equal(team.status(), 200, JSON.stringify(teamBody));
    assert.equal(teamBody.count, 2);
    assert.deepEqual(teamBody.orders.map(o => o.species), ['Pikachu', 'Eevee']);
    await page.waitForFunction(() => document.querySelectorAll('#orders .order').length === 3);
    checks.push('paste two-member team: real generation, both queued in input order, same trade code');

    const invalid = 'Pikachu\n\nNotAPokemonAtAll\n\nEevee';
    const before = (await api('/api/state')).body.orders.length;
    await page.locator('#team-text').fill(invalid);
    const badResponse = page.waitForResponse(r => r.url().endsWith('/api/orders') && r.request().method() === 'POST');
    await page.locator('#send').click();
    assert.equal((await badResponse).status(), 400);
    await page.waitForFunction(() => document.getElementById('form-message').textContent.includes('第 2 只'));
    assert.equal(await page.locator('#team-text').inputValue(), invalid);
    assert.equal((await api('/api/state')).body.orders.length, before);
    checks.push('invalid second block: nothing partially enqueued, original input retained');

    const request = { text:'Eevee\nShiny: No\nLevel: 100', requestId:crypto.randomUUID() };
    const a = await api('/api/orders','POST',request), b = await api('/api/orders','POST',request);
    assert.equal(a.status,200,JSON.stringify(a.body)); assert.equal(b.status,200);
    assert.equal(a.body.orders[0].id,b.body.orders[0].id);
    assert.equal((await api('/api/state')).body.orders.length,before+1);
    assert.equal((await api('/api/connect','POST',{port:1},false)).status,403);
    checks.push('idempotent retry creates one order; cross-site-style write without header rejected');

    await page.reload();
    await page.waitForFunction(() => !document.getElementById('send').disabled);
    assert.equal(await page.locator('#team-text').inputValue(),invalid);
    await page.screenshot({ path:path.join(out,'desktop.png'),fullPage:true });
    for (const width of [390,320]) {
      await page.setViewportSize({width,height:844});
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Horizontal overflow at '+width);
    }
    await page.screenshot({ path:path.join(out,'mobile.png'),fullPage:true });
    checks.push('draft survives reload; 390px and 320px layouts do not overflow');
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:checks,hardwareTradeTested:false,pageErrors:errors},null,2));
    console.log(JSON.stringify({passed:checks,hardwareTradeTested:false,pageErrors:errors},null,2));
  } finally {
    if (browser) await browser.close();
    if (server && server.exitCode === null) server.kill();
    fs.writeFileSync(path.join(out,'server.log'),log);
  }
})().catch(error => { console.error(error); process.exitCode=1; });
