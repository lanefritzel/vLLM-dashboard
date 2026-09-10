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
})();
