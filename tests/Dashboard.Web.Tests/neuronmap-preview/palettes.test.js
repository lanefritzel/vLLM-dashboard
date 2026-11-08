// Run with: node --test tests/Dashboard.Web.Tests/neuronmap-preview/palettes.test.js
// Checks every neuron-map palette returns real colors and behaves sensibly. No browser needed.
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

global.window = {};
global.document = { createElement: () => ({ getContext: () => ({ createImageData: () => ({ data: [] }) }) }) };
global.performance = { now: () => 0 };
global.requestAnimationFrame = () => 0;
global.atob = (s) => Buffer.from(s, 'base64').toString('binary');
require(path.join(__dirname, '../../../src/Dashboard.Web/wwwroot/js/neuronmap.js'));

const PALETTES = window.NeuronMapRenderer.PALETTES;
const ROWS = 64, COLS = 136;
const lum = (px) => 0.2126 * px[0] + 0.7152 * px[1] + 0.0722 * px[2];
function colorOf(p, h, l = 10, c = 20, sg = 0) {
  const px = [0, 0, 0, 0];
  p.write(px, 0, h, l, c, sg, ROWS, COLS, 4);
  return px;
}

test('there are plenty of palettes, with unique ids and labels', () => {
  assert.ok(PALETTES.length >= 14);
  assert.strictEqual(new Set(PALETTES.map((p) => p.id)).size, PALETTES.length);
  for (const p of PALETTES) {
    assert.ok(p.id && p.label && p.hint && p.css.startsWith('linear-gradient('), p.id);
  }
  assert.ok(PALETTES.some((p) => p.id === 'heat'), 'default palette exists');
});

test('every palette returns finite colors in range for every kind of cell', () => {
  for (const p of PALETTES) {
    for (const h of [0, 0.01, 0.25, 0.5, 0.75, 1]) {
      for (const l of [0, 3, 32, 63]) {
        for (const c of [0, 70, 135]) {
          for (const sg of [0, 1]) {
            const px = colorOf(p, h, l, c, sg);
            for (let k = 0; k < 3; k++) {
              assert.ok(Number.isFinite(px[k]), `${p.id} h=${h} l=${l} c=${c}: not finite`);
              assert.ok(px[k] >= -0.5 && px[k] <= 255.5, `${p.id} h=${h}: ${px[k]} out of range`);
            }
          }
        }
      }
    }
  }
});

test('a quiet cell is darker than a busy one in every palette', () => {
  for (const p of PALETTES) {
    if (p.id === 'twosided') continue;   // quiet = middle of the scale, checked separately
    assert.ok(lum(colorOf(p, 0)) < lum(colorOf(p, 1)), `${p.id}: quiet should be darker than busy`);
  }
});

test('two-sided: pushed up is warm, pushed down is cool, quiet is dark', () => {
  const p = PALETTES.find((x) => x.id === 'twosided');
  const up = colorOf(p, 1, 10, 20, 0), down = colorOf(p, 1, 10, 20, 1), quiet = colorOf(p, 0);
  assert.ok(up[0] > up[2], 'up should lean red/orange');
  assert.ok(down[2] > down[0], 'down should lean blue');
  assert.ok(lum(quiet) < lum(up) && lum(quiet) < lum(down));
});

test('layer type: attention layers and the others get different hues at the same glow', () => {
  const p = PALETTES.find((x) => x.id === 'layertype');
  const linear = colorOf(p, 0.6, 0), full = colorOf(p, 0.6, 3);   // every 4th layer is full attention
  assert.ok(linear[1] > linear[0], 'teal-ish');
  assert.ok(full[2] > full[1], 'purple-ish');
});

test('depth and neuron rainbows change hue along their axis', () => {
  const d = PALETTES.find((x) => x.id === 'depth'), n = PALETTES.find((x) => x.id === 'neuron');
  assert.notDeepStrictEqual(colorOf(d, 0.8, 0), colorOf(d, 0.8, 63));
  assert.notDeepStrictEqual(colorOf(n, 0.8, 10, 0), colorOf(n, 0.8, 10, 135));
  assert.deepStrictEqual(colorOf(d, 0.8, 5, 0), colorOf(d, 0.8, 5, 135), 'depth ignores the column');
});
