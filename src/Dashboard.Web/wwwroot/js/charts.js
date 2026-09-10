// Chart.js bridge for the vLLM Dashboard. All charts are live line charts driven by
// SignalR `samples` events via chart_push. Colors come from the theme's CSS variables
// so dark/light swap cleanly.
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
      text: cssVar('--text', '#e6e9ef'),
      muted: cssVar('--muted', '#8b93a7'),
      grid: cssVar('--border', '#232a3a'),
      accent: cssVar('--accent', '#4c8dff'),
      green: cssVar('--green', '#31c06b'),
      red: cssVar('--red', '#e5484d'),
      warn: cssVar('--warn', '#f5a524')
    };
  }

  function canvas(id) {
    var el = document.getElementById(id);
    return el ? el.getContext('2d') : null;
  }

  // Create a line chart. `series` = [{key,label,color}]; `opts` = {yMin,yMax,beginAtZero}.
  window.chart_create = function (id, series, opts) {
    var c = canvas(id);
    if (!c) return;
    if (charts[id]) { try { charts[id].destroy(); } catch (e) {} delete charts[id]; }
    var pal = colors();
    var hiddenKeys = (opts && opts.hidden) || [];
    var datasets = series.map(function (s) {
      var col = s.color || pal.accent;
      return {
        key: s.key, label: s.label, data: [],
        borderColor: col, backgroundColor: col + '22',
        pointRadius: 0, borderWidth: 2, tension: 0.2, fill: false, spanGaps: true,
        hidden: hiddenKeys.indexOf(s.key) !== -1
      };
    });
    var y = {};
    if (opts && opts.yMin != null) { y.min = opts.yMin; y.beginAtZero = false; }
    else if (opts && opts.beginAtZero === false) { y.beginAtZero = false; }
    else { y.beginAtZero = true; }
    if (opts && opts.yMax != null) y.max = opts.yMax;
    var config = {
      type: 'line',
      data: { labels: [], datasets: datasets },
      options: {
        responsive: true, maintainAspectRatio: false, animation: false,
        interaction: { mode: 'nearest', intersect: false },
        scales: {
          x: { ticks: { color: pal.muted, maxTicksLimit: 6, maxRotation: 0 }, grid: { color: pal.grid } },
          y: Object.assign({ ticks: { color: pal.muted }, grid: { color: pal.grid } }, y)
        },
        plugins: {
          legend: { labels: { color: pal.text, boxWidth: 12 } },
          tooltip: { intersect: false }
        }
      }
    };
    charts[id] = new Chart(c, config);
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
})();
