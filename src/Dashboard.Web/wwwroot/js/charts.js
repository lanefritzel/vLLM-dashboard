// Chart.js bridge for the vLLM Dashboard. All charts are live line charts driven by
// SignalR `samples` events via chart_push. Colors come from the theme's CSS variables
// so dark/light swap cleanly (Style Refactor P1.6: chart defaults + theme bridge).
//
// Public API: the original chart_* names are the stable entry points existing
// pages already call. createChart/applyChartTheme/chartTokens are the same
// behavior under the names the Style Refactor plan asks ChartCard/P3 work to
// use going forward — aliases, not a second implementation.
(function () {
  var charts = {};

  function cssVar(name, fallback) {
    try {
      var v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
      return v || fallback;
    } catch (e) { return fallback; }
  }

  function colors() {
    return {
      text: cssVar('--ui-text', '#e8ecf4'),
      text2: cssVar('--ui-text-2', '#c3cad9'),
      muted: cssVar('--ui-muted', '#98a2ba'),
      grid: cssVar('--ui-chart-grid', '#1c2537'),
      surface: cssVar('--ui-surface', '#111827'),
      border: cssVar('--ui-border-strong', '#2e3b52'),
      accent: cssVar('--ui-chart-2', '#5b9dff'),
      green: cssVar('--ui-chart-1', '#2fd07c'),
      red: cssVar('--ui-danger', '#f0566e'),
      warn: cssVar('--ui-warn', '#f2b23e'),
      violet: cssVar('--ui-chart-3', '#8b7cf6'),
      cyan: cssVar('--ui-chart-4', '#22d3ee'),
      pink: cssVar('--ui-chart-5', '#f472b6')
    };
  }

  function resolveColor(c) {
    if (!c) return null;
    var m = /^var\(\s*(--[\w-]+)\s*\)$/.exec(c);
    if (m) return cssVar(m[1], null) || c;
    return c;
  }

  // 8% alpha fill, used when a chart has exactly one series (a fill reads
  // as noise once two or more lines overlap it) -- unless a series opts
  // in/out explicitly via its `fill` property.
  function fillAlphaHex(col) { return col + '14'; }

  function tooltipTheme(pal) {
    return {
      enabled: true,
      intersect: false,
      backgroundColor: pal.surface,
      borderColor: pal.border,
      borderWidth: 1,
      cornerRadius: 8,
      padding: 10,
      titleColor: pal.text,
      titleFont: { size: 12, weight: '600' },
      bodyColor: pal.text2,
      bodyFont: { size: 12 }
    };
  }

  function canvas(id) {
    var el = document.getElementById(id);
    return el ? el.getContext('2d') : null;
  }

  // Per-chart hidden-series state, persisted per browser so a legend click
  // (e.g. hiding "prompt" on the throughput chart) survives a page reload.
  function hiddenStorageKey(id) { return 'ui_chart_hidden:' + id; }

  function loadHiddenKeys(id) {
    try {
      var raw = localStorage.getItem(hiddenStorageKey(id));
      return raw ? JSON.parse(raw) : [];
    } catch (e) { return []; }
  }

  function saveHiddenKeys(id, keys) {
    try { localStorage.setItem(hiddenStorageKey(id), JSON.stringify(keys)); } catch (e) { /* storage unavailable */ }
  }

  // Create a line chart. `series` = [{key,label,color}]; `opts` = {yMin,yMax,
  // beginAtZero,xGrid,yTicksLimit,sparkline}. `sparkline: true` hides axes
  // and the legend for a StatCard-sized mini chart (Style Refactor D2) --
  // used alongside, not instead of, a full ChartCard view of the same data.
  // Returns true if the canvas existed and the chart was created, false
  // otherwise -- the canvas can genuinely not exist yet on the caller's first
  // attempt (e.g. right after an @if block flips true but the DOM hasn't
  // caught up), so callers must check this rather than assume success.
  window.chart_create = function (id, series, opts) {
    var c = canvas(id);
    if (!c) return false;
    if (charts[id]) { try { charts[id].destroy(); } catch (e) {} delete charts[id]; }
    var pal = colors();
    var hiddenKeys = (opts && opts.hidden) || loadHiddenKeys(id);
    var singleSeries = series.length === 1;
    var datasets = series.map(function (s) {
      var col = resolveColor(s.color) || pal.accent;
      var filled = s.fill !== undefined ? !!s.fill : singleSeries;
      return {
        key: s.key, label: s.label, data: [],
        _colorSpec: s.color,
        _fill: filled,
        borderColor: col,
        backgroundColor: filled ? fillAlphaHex(col) : col + '22',
        pointRadius: 0, pointHoverRadius: 3, borderWidth: 1.75, tension: 0.25,
        fill: filled, spanGaps: true,
        hidden: hiddenKeys.indexOf(s.key) !== -1
      };
    });
    var y = {};
    if (opts && opts.yMin != null) { y.min = opts.yMin; y.beginAtZero = false; }
    else if (opts && opts.beginAtZero === false) { y.beginAtZero = false; }
    else { y.beginAtZero = true; }
    if (opts && opts.yMax != null) y.max = opts.yMax;
    var sparkline = !!(opts && opts.sparkline);
    var config = {
      type: 'line',
      data: { labels: [], datasets: datasets },
      options: {
        responsive: true, maintainAspectRatio: false, animation: false,
        layout: sparkline ? { padding: 0 } : undefined,
        interaction: { mode: 'nearest', intersect: false },
        scales: {
          x: {
            display: !sparkline,
            ticks: { color: pal.muted, font: { size: 11 }, maxTicksLimit: 6, maxRotation: 0 },
            grid: { display: !!(opts && opts.xGrid), color: pal.grid }
          },
          y: Object.assign({
            display: !sparkline,
            ticks: { color: pal.muted, font: { size: 11 }, maxTicksLimit: (opts && opts.yTicksLimit) || 5 },
            grid: { color: pal.grid }
          }, y)
        },
        plugins: {
          legend: {
            display: !sparkline,
            labels: { color: pal.text, boxWidth: 12 },
            // Same toggle Chart.js does by default -- reimplemented so the
            // resulting hidden-series set can be persisted per browser.
            onClick: function (e, legendItem, legend) {
              var ci = legend.chart;
              var index = legendItem.datasetIndex;
              if (ci.isDatasetVisible(index)) {
                ci.hide(index);
                legendItem.hidden = true;
              } else {
                ci.show(index);
                legendItem.hidden = false;
              }
              var hiddenNow = ci.data.datasets
                .map(function (d, i) { return { key: d.key, visible: ci.isDatasetVisible(i) }; })
                .filter(function (x) { return !x.visible; })
                .map(function (x) { return x.key; });
              saveHiddenKeys(id, hiddenNow);
            }
          },
          tooltip: tooltipTheme(pal)
        }
      }
    };
    charts[id] = new Chart(c, config);
    return true;
  };

  // Push one sample. `label` is the x-axis (timestamp), `values` maps dataset key -> value.
  window.chart_push = function (id, label, values, max) {
    var chart = charts[id];
    if (!chart) return;
    chart.data.labels.push(label);
    chart.data.datasets.forEach(function (ds) {
      var v = (values && values[ds.key] != null) ? values[ds.key] : null;
      ds.data.push(v);
    });
    var cap = max || 900;
    while (chart.data.labels.length > cap) {
      chart.data.labels.shift();
      chart.data.datasets.forEach(function (ds) { if (ds.data.length) ds.data.shift(); });
    }
    chart.update('none');
  };

  // Batch: push the same x-label to several charts at once. `groups` = {chartId: valuesByKey}.
  window.chart_push_all = function (label, groups, max) {
    Object.keys(groups).forEach(function (id) {
      window.chart_push(id, label, groups[id], max);
    });
  };

  // Show/hide one dataset by key without rebuilding the chart (used by series
  // toggle checkboxes, e.g. generation vs. prompt throughput).
  window.chart_set_visible = function (id, key, visible) {
    var chart = charts[id];
    if (!chart) return;
    var ds = chart.data.datasets.find(function (d) { return d.key === key; });
    if (!ds) return;
    ds.hidden = !visible;
    chart.update('none');
  };

  window.chart_reset = function (id) {
    var chart = charts[id];
    if (!chart) return;
    chart.data.labels = [];
    chart.data.datasets.forEach(function (ds) { ds.data = []; });
    chart.update('none');
  };

  window.chart_dispose = function (id) {
    if (charts[id]) { try { charts[id].destroy(); } catch (e) {} delete charts[id]; }
  };

  window.chart_dispose_all = function () {
    Object.keys(charts).forEach(function (k) { window.chart_dispose(k); });
  };

  // Update one live chart's theme-dependent colors in place (grid/ticks/
  // legend/tooltip/dataset border+fill) and re-render without rebuilding it.
  // Falls back to doing nothing for a chart mid-destroy.
  function applyChartTheme(chart) {
    if (!chart) return;
    var pal = colors();
    var o = chart.options;
    if (o.scales) {
      ['x', 'y'].forEach(function (axis) {
        if (!o.scales[axis]) return;
        if (o.scales[axis].ticks) o.scales[axis].ticks.color = pal.muted;
        if (o.scales[axis].grid) o.scales[axis].grid.color = pal.grid;
      });
    }
    if (o.plugins && o.plugins.legend && o.plugins.legend.labels) {
      o.plugins.legend.labels.color = pal.text;
    }
    if (o.plugins && o.plugins.tooltip) {
      Object.assign(o.plugins.tooltip, tooltipTheme(pal));
    }
    var singleSeries = chart.data.datasets.length === 1;
    chart.data.datasets.forEach(function (ds) {
      var col = resolveColor(ds._colorSpec) || pal.accent;
      var filled = ds._fill !== undefined ? !!ds._fill : singleSeries;
      ds.borderColor = col;
      ds.backgroundColor = filled ? fillAlphaHex(col) : col + '22';
    });
    try { chart.update('none'); } catch (e) { /* chart may be mid-destroy */ }
  }

  window.addEventListener('themechange', function () {
    Object.keys(charts).forEach(function (id) { applyChartTheme(charts[id]); });
  });

  // Named aliases per the Style Refactor P1.6 helper API.
  window.chartTokens = colors;
  window.createChart = window.chart_create;
  window.applyChartTheme = function (id) { applyChartTheme(charts[id]); };
})();
