// Neuron map (Overview section): draws the vLLM visualizer plugin's snapshots.
//
// Two parts:
//  1. NeuronMapRenderer -- pure canvas drawing, no SignalR, so it can be tested and previewed
//     on its own (see tests/Dashboard.Web.Tests and plugins/vllm-visualizer/dev/preview.html).
//  2. A small SignalR bridge (own hub connection, like the traffic view) that feeds it.
//
// Snapshot shape (from the plugin): { seq, ts, layers, buckets, layer_max[], max, data(base64 uint8,
// layers*buckets, each layer scaled to its own maximum) }.
(function () {
  var HOT_THRESHOLD = 0.55;   // a cell counts as "busy for this word" above this (0..1)
  var HOT_PER_LAYER = 6;      // at most this many boxed cells per layer
  var HALF_LIFE_MS = 700;     // how fast the trail of earlier words fades
  var BOX_LIFE_MS = 450;      // how long a box stays after its word
  var ROW_PX = 6;
  var LEFT = 64, STRIP = 6, GAP = 5, BAR = 44;
  var TEAL = '#1D9E75', PURPLE = '#7F77DD';

  // ---- palettes ------------------------------------------------------------------------------
  // Each palette turns a cell's glow (0..1) into a color. Ramp palettes use a 256-step lookup
  // table; the structural ones (layer type, depth, neuron position, two-sided) compute per cell.
  function lutFrom(stops) {
    var lut = new Uint8Array(256 * 3), i, k;
    for (i = 0; i < 256; i++) {
      var t = i / 255;
      for (k = 1; k < stops.length && t > stops[k][0]; k++) {}
      var a = stops[Math.max(0, k - 1)], b = stops[Math.min(k, stops.length - 1)];
      var u = b[0] === a[0] ? 0 : (t - a[0]) / (b[0] - a[0]);
      for (var ch = 0; ch < 3; ch++) lut[i * 3 + ch] = a[1][ch] + (b[1][ch] - a[1][ch]) * u;
    }
    return lut;
  }
  function css(stops) {
    return 'linear-gradient(90deg,' + stops.map(function (s) {
      return 'rgb(' + s[1].join(',') + ') ' + Math.round(s[0] * 100) + '%';
    }).join(',') + ')';
  }
  function hsl(h, s, l) {   // h 0..360, s/l 0..1 -> [r,g,b]
    var c = (1 - Math.abs(2 * l - 1)) * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = l - c / 2, r = 0, g = 0, b = 0;
    if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
    else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
    return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
  }
  function ramp(id, label, hint, stops) {
    var lut = lutFrom(stops);
    return {
      id: id, label: label, hint: hint, css: css(stops),
      write: function (px, o, h) {
        var i = ((Math.pow(h, 0.8) * 255) | 0) * 3;
        px[o] = lut[i]; px[o + 1] = lut[i + 1]; px[o + 2] = lut[i + 2];
      }
    };
  }
  var TEAL_STOPS = [[0, [14, 24, 26]], [0.5, [20, 120, 96]], [1, [150, 245, 210]]];
  var PURP_STOPS = [[0, [20, 18, 34]], [0.5, [100, 88, 200]], [1, [210, 204, 255]]];
  var DIV_STOPS = [[0, [60, 140, 255]], [0.3, [28, 60, 130]], [0.5, [16, 17, 22]], [0.7, [130, 66, 26]], [1, [255, 160, 50]]];
  var lutTeal = lutFrom(TEAL_STOPS), lutPurp = lutFrom(PURP_STOPS), lutDiv = lutFrom(DIV_STOPS);

  var PALETTES = [
    ramp('heat', 'Heat', 'Fresh = white-yellow, then orange, magenta, indigo as it fades',
      [[0, [12, 10, 26]], [0.2, [60, 20, 104]], [0.42, [150, 34, 108]], [0.65, [232, 100, 40]], [0.85, [250, 190, 60]], [1, [252, 245, 170]]]),
    ramp('fire', 'Fire', 'Black, ember red, orange, white-hot',
      [[0, [8, 4, 4]], [0.25, [110, 12, 8]], [0.5, [220, 70, 10]], [0.75, [255, 176, 30]], [1, [255, 250, 215]]]),
    ramp('magma', 'Magma', 'Black, purple, pink, cream',
      [[0, [4, 3, 12]], [0.25, [60, 16, 100]], [0.5, [150, 40, 118]], [0.75, [240, 100, 100]], [1, [252, 240, 200]]]),
    ramp('plasma', 'Plasma', 'Blue-violet, magenta, orange, yellow',
      [[0, [14, 6, 134]], [0.25, [110, 3, 167]], [0.5, [190, 50, 120]], [0.75, [236, 120, 62]], [1, [240, 249, 33]]]),
    ramp('viridis', 'Viridis', 'Purple, blue, green, yellow (color-blind friendly)',
      [[0, [68, 1, 84]], [0.25, [59, 82, 139]], [0.5, [33, 145, 140]], [0.75, [94, 201, 98]], [1, [253, 231, 37]]]),
    ramp('ocean', 'Ocean', 'Deep blue, cyan, ice white',
      [[0, [6, 10, 24]], [0.3, [10, 50, 120]], [0.6, [20, 150, 200]], [0.85, [120, 220, 235]], [1, [240, 252, 255]]]),
    ramp('neon', 'Neon', 'Violet, cyan, lime, white',
      [[0, [8, 6, 20]], [0.25, [90, 30, 200]], [0.5, [20, 190, 230]], [0.75, [100, 250, 140]], [1, [255, 255, 255]]]),
    ramp('matrix', 'Matrix', 'Terminal green on black',
      [[0, [4, 10, 6]], [0.4, [10, 90, 36]], [0.75, [40, 200, 80]], [1, [200, 255, 210]]]),
    ramp('spectrum', 'Spectrum', 'Classic scientific blue, cyan, green, yellow, red',
      [[0, [16, 20, 60]], [0.2, [30, 80, 220]], [0.4, [30, 200, 200]], [0.6, [90, 210, 70]], [0.8, [250, 220, 40]], [1, [240, 50, 40]]]),
    ramp('contrast', 'Blue-yellow', 'High contrast, safe for most color vision',
      [[0, [0, 0, 0]], [0.5, [20, 60, 200]], [1, [255, 235, 0]]]),
    ramp('gray', 'Grayscale', 'Plain black to white',
      [[0, [10, 10, 10]], [1, [245, 245, 245]]]),
    ramp('steel', 'Steel', 'The original quiet blue-gray',
      [[0, [23, 26, 32]], [1, [168, 188, 222]]]),
    {
      id: 'layertype', label: 'Layer type', hint: 'Teal rows read the past, purple rows look back at everything',
      css: 'linear-gradient(90deg,' + 'rgb(20,120,96),rgb(150,245,210) 45%,rgb(100,88,200) 55%,rgb(210,204,255))',
      write: function (px, o, h, l, c, sg, rows, cols, fullEvery) {
        var i = ((Math.pow(h, 0.8) * 255) | 0) * 3, lut = ((l + 1) % fullEvery === 0) ? lutPurp : lutTeal;
        px[o] = lut[i]; px[o + 1] = lut[i + 1]; px[o + 2] = lut[i + 2];
      }
    },
    {
      id: 'depth', label: 'Depth rainbow', hint: 'Each layer gets its own hue, from the first (blue) to the last (red)',
      css: 'linear-gradient(90deg,hsl(240,85%,45%),hsl(180,85%,45%),hsl(120,85%,45%),hsl(60,85%,50%),hsl(0,85%,50%))',
      write: function (px, o, h, l, c, sg, rows) {
        var rgb = hsl(240 - 240 * (l / Math.max(1, rows - 1)), 0.85, 0.06 + 0.58 * Math.pow(h, 0.8));
        px[o] = rgb[0]; px[o + 1] = rgb[1]; px[o + 2] = rgb[2];
      }
    },
    {
      id: 'neuron', label: 'Neuron rainbow', hint: 'Each group of neurons keeps its own hue, so you can follow it down the layers',
      css: 'linear-gradient(90deg,hsl(0,85%,50%),hsl(60,85%,50%),hsl(120,85%,45%),hsl(200,85%,50%),hsl(280,85%,55%))',
      write: function (px, o, h, l, c, sg, rows, cols) {
        var rgb = hsl(300 * (c / Math.max(1, cols - 1)), 0.85, 0.06 + 0.58 * Math.pow(h, 0.8));
        px[o] = rgb[0]; px[o + 1] = rgb[1]; px[o + 2] = rgb[2];
      }
    },
    {
      id: 'twosided', label: 'Two-sided', hint: 'Orange = pushed up, blue = pushed down (needs plugin 0.2+; otherwise all orange)',
      css: css(DIV_STOPS),
      write: function (px, o, h, l, c, sg) {
        var t = 0.5 + 0.5 * (sg ? -1 : 1) * Math.pow(h, 0.8), i = ((t * 255) | 0) * 3;
        px[o] = lutDiv[i]; px[o + 1] = lutDiv[i + 1]; px[o + 2] = lutDiv[i + 2];
      }
    }
  ];
  var PALETTE_BY_ID = {};
  PALETTES.forEach(function (p) { PALETTE_BY_ID[p.id] = p; });
  var DEFAULT_PALETTE = 'heat';
  var DEFAULT_GAP = 1;

  function Renderer(canvas, opts) {
    opts = opts || {};
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.rowPx = opts.rowPx || ROW_PX;
    this.gap = Math.max(0, Math.min(3, opts.gap == null ? DEFAULT_GAP : opts.gap | 0));   // px between cells
    this.fullEvery = opts.fullEvery || 4;       // every Nth layer is full attention (Qwen3.5 hybrid)
    this.scale = opts.scale || 'layer';         // 'layer' | 'global'
    this.palette = PALETTE_BY_ID[opts.palette] || PALETTE_BY_ID[DEFAULT_PALETTE];
    this.onStats = opts.onStats || null;
    this.rows = 64; this.cols = 136;
    this.heat = null; this.hot = []; this.hotAt = 0;
    this.layerMax = []; this.gmax = 0;
    this.snaps = 0; this.fpsWindow = []; this.lastStats = 0;
    this.raf = 0; this.lastFrame = 0; this.dirty = true;
    this.off = document.createElement('canvas');
    this._alloc();
    var self = this;
    this._ro = (typeof ResizeObserver !== 'undefined')
      ? new ResizeObserver(function () { self._resize(); self.dirty = true; self._kick(); }) : null;
    if (this._ro && canvas.parentElement) this._ro.observe(canvas.parentElement);
    this._resize();
    this._kick();
  }

  Renderer.prototype._alloc = function () {
    this.heat = new Float32Array(this.rows * this.cols);
    this.neg = new Uint8Array(this.rows * this.cols);   // 1 = this cell was last pushed down (two-sided palette)
    this.off.width = this.cols; this.off.height = this.rows;
    this.img = this.off.getContext('2d').createImageData(this.cols, this.rows);
  };

  Renderer.prototype._resize = function () {
    var dpr = window.devicePixelRatio || 1;
    var w = Math.max(320, (this.canvas.parentElement ? this.canvas.parentElement.clientWidth : 800));
    var h = this.rows * this.rowPx + 8;
    this.cssW = w; this.cssH = h;
    this.canvas.style.height = h + 'px';
    this.canvas.width = Math.round(w * dpr); this.canvas.height = Math.round(h * dpr);
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  };

  Renderer.prototype.setPalette = function (id) {
    this.palette = PALETTE_BY_ID[id] || PALETTE_BY_ID[DEFAULT_PALETTE];
    this.dirty = true; this._kick();
  };

  Renderer.prototype.setGap = function (px) {
    this.gap = Math.max(0, Math.min(3, px | 0));
    this.dirty = true; this._kick();
  };

  Renderer.prototype.setScale = function (mode) {
    this.scale = mode === 'global' ? 'global' : 'layer';
    this.dirty = true; this._kick();
  };

  Renderer.prototype.clear = function () {
    if (this.heat) this.heat.fill(0);
    this.hot = []; this.dirty = true; this._kick();
  };

  Renderer.prototype.setSnapshot = function (s) {
    if (!s || !s.data || !s.layers || !s.buckets) return;
    if (s.layers !== this.rows || s.buckets !== this.cols) {
      this.rows = s.layers; this.cols = s.buckets; this._alloc(); this._resize();
    }
    var bin = atob(s.data);
    if (bin.length !== this.rows * this.cols) return;
    this.layerMax = s.layer_max || [];
    var gmax = 0, l, c;
    for (l = 0; l < this.layerMax.length; l++) if (this.layerMax[l] > gmax) gmax = this.layerMax[l];
    this.gmax = gmax || 1;

    var sgn = s.sign ? atob(s.sign) : null;   // bit-packed, 1 = dominant direction is negative
    var hot = [];
    for (l = 0; l < this.rows; l++) {
      var k = (this.scale === 'global' && this.layerMax[l] && gmax) ? this.layerMax[l] / gmax : 1;
      var cand = [];
      for (c = 0; c < this.cols; c++) {
        var v = (bin.charCodeAt(l * this.cols + c) / 255) * k;
        var i = l * this.cols + c;
        if (v > this.heat[i]) {
          this.heat[i] = v;
          this.neg[i] = sgn ? ((sgn.charCodeAt(i >> 3) >> (7 - (i & 7))) & 1) : 0;
        }
        if (v >= HOT_THRESHOLD) cand.push([v, c]);
      }
      cand.sort(function (a, b) { return b[0] - a[0]; });
      for (c = 0; c < cand.length && c < HOT_PER_LAYER; c++) hot.push([l, cand[c][1]]);
    }
    this.hot = hot; this.hotAt = performance.now();
    this.snaps++;
    var now = performance.now();
    this.fpsWindow.push(now);
    while (this.fpsWindow.length && now - this.fpsWindow[0] > 2000) this.fpsWindow.shift();
    this.dirty = true; this._kick();
  };

  Renderer.prototype._kick = function () {
    if (this.raf) return;
    var self = this;
    this.raf = requestAnimationFrame(function (t) { self.raf = 0; self._frame(t); });
  };

  Renderer.prototype._frame = function (t) {
    var dt = this.lastFrame ? Math.min(250, t - this.lastFrame) : 16;
    if (dt < 30 && !this.dirty) { this._kick(); return; }   // ~30 fps cap
    this.lastFrame = t;
    var decay = Math.pow(0.5, dt / HALF_LIFE_MS), alive = false, i, n = this.heat.length;
    for (i = 0; i < n; i++) {
      var h = this.heat[i] * decay;
      if (h < 0.01) h = 0; else alive = true;
      this.heat[i] = h;
    }
    var boxesAlive = this.hot.length && (performance.now() - this.hotAt) < BOX_LIFE_MS;
    this._draw();
    this._stats();
    this.dirty = false;
    if (alive || boxesAlive) this._kick();   // go idle when nothing is moving
    else this.lastFrame = 0;
  };

  Renderer.prototype._draw = function () {
    var ctx = this.ctx, W = this.cssW, H = this.cssH, rows = this.rows, cols = this.cols;
    ctx.fillStyle = '#0f1115'; ctx.fillRect(0, 0, W, H);
    var px = this.img.data, i, o, l, c, pal = this.palette;
    for (l = 0; l < rows; l++) {
      for (c = 0; c < cols; c++) {
        i = l * cols + c; o = i * 4;
        pal.write(px, o, this.heat[i], l, c, this.neg[i], rows, cols, this.fullEvery);
        px[o + 3] = 255;
      }
    }
    this.off.getContext('2d').putImageData(this.img, 0, 0);
    var plotX = LEFT + STRIP + GAP, plotW = W - plotX - BAR - GAP * 2, rowH = this.rowPx;
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(this.off, plotX, 4, plotW, rows * rowH);
    if (this.gap > 0) {   // thin background lines between neighbouring cells
      var g = this.gap, cw = plotW / cols, gx, gy;
      ctx.fillStyle = '#0f1115';
      for (gx = 1; gx < cols; gx++) ctx.fillRect(Math.round(plotX + gx * cw - g / 2), 4, g, rows * rowH);
      for (gy = 1; gy < rows; gy++) ctx.fillRect(plotX, 4 + gy * rowH - Math.floor(g / 2), plotW, g);
    }

    ctx.font = '11px sans-serif'; ctx.textBaseline = 'middle';
    var r;
    for (r = 0; r < rows; r++) {
      ctx.fillStyle = ((r + 1) % this.fullEvery === 0) ? PURPLE : TEAL;
      ctx.fillRect(LEFT, 4 + r * rowH, STRIP, rowH - 1);
    }
    ctx.fillStyle = '#8a8f98';
    var marks = [1, Math.round(rows / 4), Math.round(rows / 2), Math.round(rows * 3 / 4), rows], m;
    for (m = 0; m < marks.length; m++) ctx.fillText('layer ' + marks[m], 2, 4 + (marks[m] - 1) * rowH + rowH / 2);

    // loudness bar per layer (raw maximum, relative to the loudest layer)
    var barX = W - BAR - GAP;
    for (r = 0; r < rows && r < this.layerMax.length; r++) {
      var f = this.gmax ? this.layerMax[r] / this.gmax : 0;
      ctx.fillStyle = f > 0.8 ? '#d6a24a' : '#566070';
      ctx.fillRect(barX, 4 + r * rowH, Math.max(1, f * BAR), rowH - 1);
    }

    var age = performance.now() - this.hotAt;
    if (this.hot.length && age < BOX_LIFE_MS) {
      ctx.strokeStyle = 'rgba(255,255,255,' + (1 - age / BOX_LIFE_MS).toFixed(2) + ')';
      ctx.lineWidth = 1.2;
      var cw = plotW / cols, k;
      for (k = 0; k < this.hot.length; k++) {
        ctx.strokeRect(plotX + this.hot[k][1] * cw - 0.5, 4 + this.hot[k][0] * rowH - 0.5, Math.max(cw, 3) + 0.5, rowH);
      }
    }
    if (!this.snaps) {
      ctx.fillStyle = '#8a8f98'; ctx.font = '13px sans-serif'; ctx.textAlign = 'center';
      ctx.fillText('Waiting for the model to work on a request…', plotX + plotW / 2, H / 2);
      ctx.textAlign = 'left';
    }
  };

  Renderer.prototype._stats = function () {
    if (!this.onStats) return;
    var now = performance.now();
    if (now - this.lastStats < 500) return;
    this.lastStats = now;
    var loud = 0, r;
    for (r = 1; r < this.layerMax.length; r++) if (this.layerMax[r] > this.layerMax[loud]) loud = r;
    this.onStats({
      perSecond: this.fpsWindow.length / 2,
      hot: this.hot.length,
      loudestLayer: this.layerMax.length ? loud + 1 : null,
      snapshots: this.snaps
    });
  };

  Renderer.prototype.destroy = function () {
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
    if (this._ro) this._ro.disconnect();
  };

  Renderer.PALETTES = PALETTES;
  window.NeuronMapRenderer = Renderer;

  // ---- SignalR bridge ------------------------------------------------------------------------
  var conn = null, joinedId = null, renderer = null, statEl = null, scaleMode = 'layer';
  var statusReceiver = null, hadData = false;

  function describe(s) {
    return s.snapshots ? (s.perSecond.toFixed(1) + ' updates/s · loudest layer ' + s.loudestLayer + ' · ' + s.hot + ' busy cells')
                       : 'no activity yet';
  }

  window.neuronmap_attach = function (canvasId, statId, fullEvery) {
    var canvas = document.getElementById(canvasId);
    if (!canvas) return false;
    if (renderer) renderer.destroy();
    statEl = statId ? document.getElementById(statId) : null;
    renderer = new Renderer(canvas, {
      fullEvery: fullEvery || 4, scale: scaleMode, palette: currentPalette(), gap: currentGap(),
      onStats: function (s) {
        if (statEl) statEl.textContent = describe(s);
        // Tell the page only when data starts/stops arriving (not every frame): it uses this to
        // explain a map that is connected but blank (usually a CUDA-graph recipe).
        var has = !!s.snapshots;
        if (has !== hadData) {
          hadData = has;
          if (statusReceiver) { try { statusReceiver.invokeMethodAsync('OnHasData', has); } catch (e) {} }
        }
      }
    });
    return true;
  };

  window.neuronmap_start = function (receiver) {
    statusReceiver = receiver; hadData = false;
    if (conn) { try { conn.stop(); } catch (e) {} conn = null; }
    var c = new signalR.HubConnectionBuilder()
      .withUrl('/visualizer-hub')
      .withAutomaticReconnect([0, 250, 1000, 5000, 10000])
      .build();
    c.on('visualizerSnapshot', function (json) {
      if (!renderer) return;
      try { renderer.setSnapshot(typeof json === 'string' ? JSON.parse(json) : json); } catch (e) {}
    });
    c.on('visualizerStatus', function (st) {
      var state = st && (st.state || st.State) || 'idle', detail = st && (st.detail || st.Detail) || '';
      receiver.invokeMethodAsync('OnStatus', state, detail);
    });
    c.onreconnected(function () { if (joinedId) c.invoke('JoinSource', joinedId).catch(function () {}); });
    conn = c;
    return c.start();
  };

  window.neuronmap_join = function (sourceId) {
    joinedId = sourceId;
    if (conn && sourceId) return conn.invoke('JoinSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.neuronmap_set_scale = function (mode) {
    scaleMode = mode;
    if (renderer) renderer.setScale(mode);
  };

  window.neuronmap_clear = function () { if (renderer) renderer.clear(); };

  function currentPalette() {
    var id = DEFAULT_PALETTE;
    try { id = localStorage.getItem('nm-palette') || DEFAULT_PALETTE; } catch (e) {}
    return PALETTE_BY_ID[id] ? id : DEFAULT_PALETTE;
  }
  function currentGap() {
    var v = DEFAULT_GAP;
    try { var raw = localStorage.getItem('nm-gap'); if (raw !== null && raw !== '') v = parseInt(raw, 10); } catch (e) {}
    return (v >= 0 && v <= 3) ? v : DEFAULT_GAP;
  }
  window.neuronmap_get_gap = currentGap;
  window.neuronmap_set_gap = function (px) {
    px = Math.max(0, Math.min(3, px | 0));
    try { localStorage.setItem('nm-gap', String(px)); } catch (e) {}
    if (renderer) renderer.setGap(px);
  };
  window.neuronmap_palettes = function () {
    return PALETTES.map(function (p) { return { id: p.id, label: p.label, hint: p.hint, css: p.css }; });
  };
  window.neuronmap_get_palette = currentPalette;
  window.neuronmap_set_palette = function (id) {
    if (!PALETTE_BY_ID[id]) return;
    try { localStorage.setItem('nm-palette', id); } catch (e) {}
    if (renderer) renderer.setPalette(id);
  };

  // Leaving matters: it lets the server close the plugin stream, which puts the plugin back to sleep.
  window.neuronmap_stop = function () {
    var c = conn, id = joinedId;
    conn = null; joinedId = null;
    if (renderer) { renderer.destroy(); renderer = null; }
    if (c) {
      var done = id ? c.invoke('LeaveSource', id).catch(function () {}) : Promise.resolve();
      done.then(function () { try { c.stop(); } catch (e) {} });
    }
  };

  window.neuronmap_pref = function (value) {
    try {
      if (value === undefined) return localStorage.getItem('nm-open') || '';
      localStorage.setItem('nm-open', value);
    } catch (e) {}
    return '';
  };
})();
