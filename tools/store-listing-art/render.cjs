const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '..', '..');
const output = path.join(root, 'StorePackage', 'ListingAssets');
const source = pathToFileURL(path.join(__dirname, 'index.html')).href;
const assets = [
  { name: 'Arsenal-Poster-1440x2160.png', kind: 'poster', width: 1440, height: 2160, alpha: false },
  { name: 'Arsenal-BoxArt-2160x2160.png', kind: 'box', width: 2160, height: 2160, alpha: false },
  { name: 'Arsenal-AppTile-300x300.png', kind: 'tile', width: 300, height: 300, alpha: true },
  { name: 'Arsenal-AppTile-150x150.png', kind: 'tile', width: 150, height: 150, alpha: true },
  { name: 'Arsenal-AppTile-71x71.png', kind: 'tile', width: 71, height: 71, alpha: true },
];

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, channel: 'msedge' });
  for (const asset of assets) {
    const page = await browser.newPage({ viewport: { width: asset.width, height: asset.height }, deviceScaleFactor: 1 });
    await page.goto(`${source}?kind=${asset.kind}`, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(output, asset.name), omitBackground: asset.alpha });
    await page.close();
  }
  await browser.close();
  process.stdout.write(`${assets.length} Store assets rendered to ${output}\n`);
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
