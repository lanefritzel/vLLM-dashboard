// SignalR client bridge. One connection per call to hub_start (per interactive island).
// Re-joins the current source group automatically on reconnect, since the server-side
// group membership is tied to the connection.
(function () {
  var conn = null;
  var currentSourceId = null;

  window.hub_start = function (receiver) {
    if (conn) { try { conn.stop(); } catch (e) {} conn = null; }
    var c = new signalR.HubConnectionBuilder()
      .withUrl('/metrics-hub')
      .withAutomaticReconnect([0, 250, 1000, 5000, 10000])
      .build();

    c.on('receiveSample', function (s) { receiver.invokeMethodAsync('OnSample', JSON.stringify(s)); });
    c.on('configUpdated', function () { receiver.invokeMethodAsync('OnConfigUpdated'); });
    c.onreconnected(function () { if (currentSourceId) c.invoke('JoinSource', currentSourceId).catch(function () {}); });

    conn = c;
    return c.start();
  };

  window.hub_join = function (sourceId) {
    currentSourceId = sourceId;
    if (conn && sourceId) return conn.invoke('JoinSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.hub_leave = function (sourceId) {
    if (conn && sourceId) return conn.invoke('LeaveSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.hub_stop = function () {
    if (conn) { try { conn.stop(); } catch (e) {} conn = null; }
  };

  // Separate connection for the live traffic view (its own hub, its own group
  // membership) — kept independent of the metrics connection above so either can
  // reconnect/stop without affecting the other.
  var trafficConn = null;
  var trafficCurrentSourceId = null;

  window.traffichub_start = function (receiver) {
    if (trafficConn) { try { trafficConn.stop(); } catch (e) {} trafficConn = null; }
    var c = new signalR.HubConnectionBuilder()
      .withUrl('/traffic-hub')
      .withAutomaticReconnect([0, 250, 1000, 5000, 10000])
      .build();

    c.on('trafficStarted', function (e) { receiver.invokeMethodAsync('OnTrafficStarted', JSON.stringify(e)); });
    c.on('trafficChunk', function (e) { receiver.invokeMethodAsync('OnTrafficChunk', JSON.stringify(e)); });
    c.on('trafficFinished', function (e) { receiver.invokeMethodAsync('OnTrafficFinished', JSON.stringify(e)); });
    c.onreconnected(function () { if (trafficCurrentSourceId) c.invoke('JoinSource', trafficCurrentSourceId).catch(function () {}); });

    trafficConn = c;
    return c.start();
  };

  window.traffichub_join = function (sourceId) {
    trafficCurrentSourceId = sourceId;
    if (trafficConn && sourceId) return trafficConn.invoke('JoinSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.traffichub_leave = function (sourceId) {
    if (trafficConn && sourceId) return trafficConn.invoke('LeaveSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.traffichub_stop = function () {
    if (trafficConn) { try { trafficConn.stop(); } catch (e) {} trafficConn = null; }
  };

  // Separate connection for live container log streaming — its own hub, its own
  // group membership, independent of the metrics and traffic connections above.
  var logsConn = null;
  var logsCurrentSourceId = null;

  window.logshub_start = function (receiver) {
    if (logsConn) { try { logsConn.stop(); } catch (e) {} logsConn = null; }
    var c = new signalR.HubConnectionBuilder()
      .withUrl('/logs-hub')
      .withAutomaticReconnect([0, 250, 1000, 5000, 10000])
      .build();

    c.on('logLine', function (line) { receiver.invokeMethodAsync('OnLogLine', line); });
    c.onreconnected(function () { if (logsCurrentSourceId) c.invoke('JoinSource', logsCurrentSourceId).catch(function () {}); });

    logsConn = c;
    return c.start();
  };

  window.logshub_join = function (sourceId) {
    logsCurrentSourceId = sourceId;
    if (logsConn && sourceId) return logsConn.invoke('JoinSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.logshub_leave = function (sourceId) {
    if (logsConn && sourceId) return logsConn.invoke('LeaveSource', sourceId).catch(function () {});
    return Promise.resolve();
  };

  window.logshub_stop = function () {
    if (logsConn) { try { logsConn.stop(); } catch (e) {} logsConn = null; }
  };

  window.logs_scroll_bottom = function (elId) {
    var el = document.getElementById(elId);
    if (el) el.scrollTop = el.scrollHeight;
  };

  // Separate connection for the swap ceremony's live progress (P14) -- its own
  // hub, its own group membership (one group per deployment id, not per
  // source), independent of every other connection above.
  var deploymentConn = null;
  var deploymentCurrentId = null;

  window.deploymenthub_start = function (receiver) {
    if (deploymentConn) { try { deploymentConn.stop(); } catch (e) {} deploymentConn = null; }
    var c = new signalR.HubConnectionBuilder()
      .withUrl('/deployment-hub')
      .withAutomaticReconnect([0, 250, 1000, 5000, 10000])
      .build();

    c.on('receiveState', function (s) { receiver.invokeMethodAsync('OnState', JSON.stringify(s)); });
    c.on('receiveLog', function (lines) {
      (Array.isArray(lines) ? lines : [lines]).forEach(function (l) {
        receiver.invokeMethodAsync('OnLog', l);
      });
    });
    c.onreconnected(function () { if (deploymentCurrentId) c.invoke('JoinDeployment', deploymentCurrentId).catch(function () {}); });

    deploymentConn = c;
    return c.start();
  };

  window.deploymenthub_join = function (deploymentId) {
    deploymentCurrentId = deploymentId;
    if (deploymentConn && deploymentId) return deploymentConn.invoke('JoinDeployment', deploymentId).catch(function () {});
    return Promise.resolve();
  };

  window.deploymenthub_leave = function (deploymentId) {
    if (deploymentConn && deploymentId) return deploymentConn.invoke('LeaveDeployment', deploymentId).catch(function () {});
    return Promise.resolve();
  };

  window.deploymenthub_stop = function () {
    if (deploymentConn) { try { deploymentConn.stop(); } catch (e) {} deploymentConn = null; }
  };

  // P15a: Instances page "[Copy]"/"[Copy client snippet]".
  window.copy_to_clipboard = function (text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).catch(function () {});
    }
    // Fallback for non-secure contexts where the Clipboard API is unavailable.
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.focus();
    ta.select();
    try { document.execCommand('copy'); } catch (e) {}
    document.body.removeChild(ta);
    return Promise.resolve();
  };
})();
