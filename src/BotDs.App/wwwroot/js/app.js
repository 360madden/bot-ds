/* ============================================================
   BotDs Dashboard — Vanilla JS, no dependencies
   ============================================================ */
(function () {
  "use strict";

  // ---- Constants ----
  const API_BASE = "";  // Same origin
  const SSE_ENDPOINT = "/api/events";
  const STATUS_ENDPOINT = "/api/status";
  const PROFILES_ENDPOINT = "/api/profiles";
  const RELOAD_PROFILES_ENDPOINT = "/api/profiles/reload";
  const ARM_ENDPOINT = "/api/control/arm";
  const DISARM_ENDPOINT = "/api/control/disarm";
  const ESTOP_ENDPOINT = "/api/control/emergency-stop";
  const CLEAR_STOP_ENDPOINT = "/api/control/clear-stop";
  const STATUS_POLL_MS = 2000;
  const MAX_LOG_ENTRIES = 500;
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 30000;

  // ---- Token management ----
  function getToken() {
    return sessionStorage.getItem("botds-token") || "";
  }

  function setToken(token) {
    if (token) {
      sessionStorage.setItem("botds-token", token);
    } else {
      sessionStorage.removeItem("botds-token");
    }
  }

  // ---- Fetch wrapper with auth ----
  async function apiFetch(path, method, body) {
    const headers = {};
    const token = getToken();
    if (token) {
      headers["Authorization"] = "Bearer " + token;
    }
    if (body !== undefined) {
      headers["Content-Type"] = "application/json";
    }
    const resp = await fetch(API_BASE + path, {
      method: method || "GET",
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!resp.ok) {
      const text = await resp.text().catch(() => "");
      throw new Error("HTTP " + resp.status + ": " + (text || resp.statusText));
    }
    const ct = resp.headers.get("content-type") || "";
    if (ct.includes("application/json")) {
      return resp.json();
    }
    return resp.text();
  }

  // ---- DOM refs ----
  const $ = (id) => document.getElementById(id);

  // Top bar
  const tokenForm = $("token-form");
  const tokenInput = $("token-input");
  const tokenClear = $("token-clear");
  const connLabel = $("conn-label");

  // Provider
  const providerHealth = $("provider-health");
  const providerProtocol = $("provider-protocol");
  const providerSession = $("provider-session");
  const providerSequence = $("provider-sequence");
  const providerFrame = $("provider-frame");
  const providerAge = $("provider-age");
  const providerFaultRow = $("provider-fault-row");
  const providerFault = $("provider-fault");

  // Scanner
  const scannerSection = $("scanner-section");
  const scannerPid = $("scanner-pid");
  const scannerStatus = $("scanner-status");
  const scannerCacheHits = $("scanner-cache-hits");
  const scannerCacheMisses = $("scanner-cache-misses");
  const scannerFullScans = $("scanner-full-scans");
  const scannerWindowHits = $("scanner-window-hits");
  const scannerWindowMisses = $("scanner-window-misses");
  const scannerReadCycles = $("scanner-read-cycles");
  const scannerReadFails = $("scanner-read-fails");

  // Controller
  const controllerState = $("controller-state");
  const controllerStop = $("controller-stop");
  const controllerDecisionRow = $("controller-decision-row");
  const controllerDecision = $("controller-decision");
  const btnArm = $("btn-arm");
  const btnDisarm = $("btn-disarm");
  const btnEstop = $("btn-estop");
  const btnClearStop = $("btn-clear-stop");

  // Arm dialog
  const armDialog = $("arm-dialog");
  const armDialogForm = $("arm-dialog-form");
  const armDialogCancel = $("arm-dialog-cancel");
  const armDialogConfirm = $("arm-dialog-confirm");
  const armReadinessLoading = $("arm-readiness-loading");
  const armReadinessResult = $("arm-readiness-result");
  const armReadinessStatus = $("arm-readiness-status");
  const armReadinessBlockers = $("arm-readiness-blockers");
  const armReadinessWarnings = $("arm-readiness-warnings");
  const armReadinessError = $("arm-readiness-error");

  // Profiles
  const profileSelect = $("profile-select");
  const btnReloadProfiles = $("btn-reload-profiles");
  const btnEditProfile = $("btn-edit-profile");
  const profileDetail = $("profile-detail");
  const profileId = $("profile-id");
  const profileCalling = $("profile-calling");
  const profileLevel = $("profile-level");
  const profileRules = $("profile-rules");
  const profileAbilities = $("profile-abilities");

  // Profile editor
  const profileEditorSection = $("profile-editor-section");
  const profileEditorTextarea = $("profile-editor-textarea");
  const editorFilename = $("editor-filename");
  const btnEditorRefresh = $("btn-editor-refresh");
  const btnEditorClose = $("btn-editor-close");
  const btnSaveProfile = $("btn-save-profile");
  const editorStatus = $("editor-status");

  // Player
  const playerName = $("player-name");
  const playerMeta = $("player-meta");
  const playerHealthMeter = $("player-health-meter");
  const playerHealthFill = $("player-health-fill");
  const playerHealthText = $("player-health-text");
  const playerResourceSection = $("player-resource-section");
  const playerResourceLabel = $("player-resource-label");
  const playerResourceFill = $("player-resource-fill");
  const playerResourceText = $("player-resource-text");
  const playerCast = $("player-cast");
  const playerCastText = $("player-cast-text");

  // Target
  const targetName = $("target-name");
  const targetMeta = $("target-meta");
  const targetHealthMeter = $("target-health-meter");
  const targetHealthFill = $("target-health-fill");
  const targetHealthText = $("target-health-text");
  const targetResourceSection = $("target-resource-section");
  const targetResourceLabel = $("target-resource-label");
  const targetResourceFill = $("target-resource-fill");
  const targetResourceText = $("target-resource-text");
  const targetCast = $("target-cast");
  const targetCastText = $("target-cast-text");

  // Sequence
  const seqNumber = $("seq-number");
  const seqAge = $("seq-age");
  const seqSession = $("seq-session");
  const seqLastUpdate = $("seq-last-update");

  // Log
  const logContainer = $("log-container");
  const logEmpty = $("log-empty");
  const logFilter = $("log-filter");
  const btnClearLog = $("btn-clear-log");
  const logAutoscroll = $("log-autoscroll");

  // Settings
  const settingsForm = $("settings-form");
  const setScannerInterval = $("set-scanner-interval");
  const setScannerMaxage = $("set-scanner-maxage");
  const setScannerProcess = $("set-scanner-process");
  const setEvalTelemetryAge = $("set-eval-telemetry-age");
  const setEvalInterval = $("set-eval-interval");
  const setDashInterval = $("set-dash-interval");
  const setDashLogLimit = $("set-dash-log-limit");
  const setLogRetained = $("set-log-retained");
  const btnSaveSettings = $("btn-save-settings");
  const settingsStatus = $("settings-status");

  // SSE
  const sseDot = $("sse-dot");
  const sseStatusText = $("sse-status-text");

  // ---- State ----
  let profilesData = [];
  let selectedProfileId = "";
  let lastStatus = null;
  let logEntries = [];
  let sseRetryCount = 0;
  let sseRetryTimer = null;
  let statusPollTimer = null;
  let sseAbortController = null;
  let sseReader = null;
  let activeProfileId = "";
  let activeProfileEnabled = false;
  let profileSelectionPending = false;

  // ---- Helpers ----
  function setText(id, text) {
    if (typeof id === "string") id = $(id);
    if (id) id.textContent = text;
  }

  function setHidden(id, hidden) {
    if (typeof id === "string") id = $(id);
    if (id) id.hidden = hidden;
  }

  function formatDuration(ms) {
    if (ms == null || isNaN(ms)) return "—";
    if (ms < 0) return "stale";
    if (ms < 1000) return ms + "ms";
    var s = Math.floor(ms / 1000);
    if (s < 60) return s + "s";
    var m = Math.floor(s / 60);
    s = s % 60;
    return m + "m " + s + "s";
  }

  function formatTime(iso) {
    if (!iso) return "—";
    try {
      var d = new Date(iso);
      return d.toLocaleTimeString();
    } catch {
      return iso;
    }
  }

  function formatSeq(n) {
    if (n == null) return "—";
    return String(n);
  }

  function healthColor(pct) {
    if (pct == null) return "var(--gray)";
    if (pct > 60) return "var(--hp-high)";
    if (pct > 25) return "var(--hp-mid)";
    return "var(--hp-low)";
  }

  function escapeHtml(s) {
    var el = document.createElement("span");
    el.textContent = s;
    return el.innerHTML;
  }

  // ---- Status badge ----
  function setStatusBadge(el, value) {
    if (!el) return;
    var text = (value || "—").replace(/([a-z])([A-Z])/g, "$1 $2");
    el.textContent = text;
    el.setAttribute("data-status", (value || "").toLowerCase());
  }

  // ---- Health bar update ----
  function updateHealthBar(meterEl, fillEl, textEl, current, maximum) {
    var pct = (current != null && maximum != null && maximum > 0)
      ? Math.min(100, Math.max(0, (current / maximum) * 100))
      : 0;
    fillEl.style.width = pct + "%";
    fillEl.style.background = healthColor(pct);
    meterEl.setAttribute("aria-valuenow", Math.round(pct));
    textEl.textContent = (current != null ? current : "—") + " / " + (maximum != null ? maximum : "—");
  }

  // ---- Resource bar update ----
  function updateResourceBar(sectionEl, labelEl, fillEl, textEl, resource) {
    if (!resource || resource.current == null) {
      sectionEl.hidden = true;
      return;
    }
    sectionEl.hidden = false;
    labelEl.textContent = resource.kind || "Resource";
    var max = resource.maximum || 1;
    var pct = Math.min(100, Math.max(0, (resource.current / max) * 100));
    fillEl.style.width = pct + "%";
    textEl.textContent = resource.current + " / " + max;
  }

  // ---- Unit display ----
  function updateUnit(prefix, unit) {
    var nameEl = $(prefix + "-name");
    var metaEl = $(prefix + "-meta");
    var healthMeter = $(prefix + "-health-meter");
    var healthFill = $(prefix + "-health-fill");
    var healthText = $(prefix + "-health-text");
    var resourceSection = $(prefix + "-resource-section");
    var resourceLabel = $(prefix + "-resource-label");
    var resourceFill = $(prefix + "-resource-fill");
    var resourceText = $(prefix + "-resource-text");
    var castEl = $(prefix + "-cast");
    var castTextEl = $(prefix + "-cast-text");

    if (!unit || !unit.id) {
      nameEl.textContent = prefix === "player" ? "No player data" : "No target";
      metaEl.textContent = "";
      updateHealthBar(healthMeter, healthFill, healthText, null, null);
      resourceSection.hidden = true;
      castEl.hidden = true;
      return;
    }

    nameEl.textContent = unit.name || unit.id || "—";
    var meta = [];
    if (unit.level != null) meta.push("Lv " + unit.level);
    if (unit.calling) meta.push(unit.calling);
    if (unit.relation) meta.push(unit.relation);
    if (unit.isPlayer) meta.push("Player");
    if (unit.inCombat) meta.push("In Combat");
    metaEl.textContent = meta.join(" · ");

    updateHealthBar(healthMeter, healthFill, healthText,
      unit.health ? unit.health.current : null,
      unit.health ? unit.health.maximum : null);

    updateResourceBar(resourceSection, resourceLabel, resourceFill, resourceText, unit.resource);

    if (unit.cast && unit.cast.isCasting) {
      castEl.hidden = false;
      castTextEl.textContent = (unit.cast.name || unit.cast.abilityId || "—") +
        (unit.cast.remainingMilliseconds != null ? " (" + formatDuration(unit.cast.remainingMilliseconds) + ")" : "");
    } else {
      castEl.hidden = true;
    }
  }

  // ---- Update connection label ----
  function updateConnLabel(health) {
    var map = {
      Disconnected: "Disconnected",
      Discovering: "Discovering…",
      Synchronizing: "Syncing…",
      Healthy: "Connected",
      Degraded: "Degraded",
      Stale: "Stale",
      Faulted: "Faulted",
    };
    connLabel.textContent = map[health] || health || "Unknown";
  }

  // ---- Full status update ----
  function applyStatus(status) {
    if (!status) return;
    lastStatus = status;

    // Provider
    var provider = status.provider || status;
    var health = provider.health || "Disconnected";
    setStatusBadge(providerHealth, health);
    updateConnLabel(health);
    setText(providerProtocol, provider.protocolVersion || "—");
    setText(providerSession, provider.sessionId || "—");
    setText(providerSequence, formatSeq(provider.sequence));
    setText(providerFrame, provider.producerFrameMilliseconds != null ? provider.producerFrameMilliseconds : "—");

    var ageMs = provider.ageMilliseconds != null ? provider.ageMilliseconds : null;
    if (provider.receivedAtUtc && ageMs == null) {
      var diff = Date.now() - new Date(provider.receivedAtUtc).getTime();
      ageMs = diff;
    }
    setText(providerAge, formatDuration(ageMs));

    if (provider.fault) {
      setHidden(providerFaultRow, false);
      setText(providerFault, provider.fault);
    } else {
      setHidden(providerFaultRow, true);
    }

    // Scanner metrics
    if (status.scanner) {
      setHidden(scannerSection, false);
      setText(scannerPid, status.scanner.isAttached ? String(status.scanner.attachmentPid) : "—");
      setStatusBadge(scannerStatus, status.scanner.lastResultHealth || "Disconnected");
      var m = status.scanner.metrics || {};
      setText(scannerCacheHits, formatSeq(m.cacheHitCount));
      setText(scannerCacheMisses, formatSeq(m.cacheMissCount));
      setText(scannerFullScans, formatSeq(m.fullScanCount));
      setText(scannerWindowHits, formatSeq(m.smallWindowHits));
      setText(scannerWindowMisses, formatSeq(m.smallWindowMisses));
      setText(scannerReadCycles, formatSeq(m.readCycleFailures != null
        ? (m.cacheHitCount || 0) + (m.cacheMissCount || 0) + (m.readCycleFailures || 0)
        : null));
      setText(scannerReadFails, formatSeq(m.readFailures));
    } else {
      setHidden(scannerSection, true);
    }

    // Controller
    var ctrl = status.controller || status;
    var state = ctrl.state || ctrl.controllerState || "Disarmed";
    setStatusBadge(controllerState, state);

    var stopReason = ctrl.stopReason || "None";
    setText(controllerStop, stopReason === "None" ? "None" : stopReason);

    if (ctrl.pendingDecision) {
      setHidden(controllerDecisionRow, false);
      setText(controllerDecision, typeof ctrl.pendingDecision === "string"
        ? ctrl.pendingDecision
        : JSON.stringify(ctrl.pendingDecision));
    } else {
      setHidden(controllerDecisionRow, true);
    }

    if (!profileSelectionPending
      && Object.prototype.hasOwnProperty.call(status, "activeProfileId")) {
      activeProfileId = status.activeProfileId || "";
      updateActiveProfileEnabled();
    } else {
      refreshControlButtons();
    }

    // Player
    updateUnit("player", status.player);

    // Target
    updateUnit("target", status.target);

    // Sequence
    setText(seqNumber, formatSeq(provider.sequence));
    setText(seqAge, formatDuration(ageMs));
    setText(seqSession, provider.sessionId || "—");
    setText(seqLastUpdate, formatTime(provider.receivedAtUtc));
  }

  // ---- Profile handling ----
  function populateProfileSelect(profiles) {
    profilesData = profiles || [];
    profileSelect.innerHTML = "";
    var emptyOpt = document.createElement("option");
    emptyOpt.value = "";
    emptyOpt.textContent = profilesData.length === 0
      ? "— no profiles loaded —"
      : "— select profile —";
    profileSelect.appendChild(emptyOpt);

    profilesData.forEach(function (p) {
      var opt = document.createElement("option");
      opt.value = p.id || "";
      opt.textContent = (p.id || "???") + " (" + (p.character ? p.character.calling : "?") + ")"
        + (p.enabled === false ? " [disabled]" : "");
      profileSelect.appendChild(opt);
    });

    profileSelect.disabled = profilesData.length === 0;
    btnReloadProfiles.disabled = false;

    // Restore selection if still present
    if (selectedProfileId && profilesData.some(function (p) { return p.id === selectedProfileId; })) {
      profileSelect.value = selectedProfileId;
      showProfileDetail(selectedProfileId);
    } else {
      selectedProfileId = "";
      profileDetail.hidden = true;
    }

    // Track whether the currently selected profile is enabled
    updateActiveProfileEnabled();
  }

  function showProfileDetail(id) {
    var p = profilesData.find(function (x) { return x.id === id; });
    if (!p) {
      profileDetail.hidden = true;
      btnEditProfile.disabled = true;
      return;
    }
    profileDetail.hidden = false;
    btnEditProfile.disabled = false;
    setText(profileId, p.id);
    setText(profileCalling, p.character ? p.character.calling : "—");
    setText(profileLevel,
      (p.character && p.character.minimumLevel != null ? p.character.minimumLevel : "?") +
      " – " +
      (p.character && p.character.maximumLevel != null ? p.character.maximumLevel : "?"));
    setText(profileRules, p.ruleCount != null ? p.ruleCount : "0");
    setText(profileAbilities, p.abilityCount != null ? p.abilityCount : "0");
  }

  function updateActiveProfileEnabled() {
    if (!activeProfileId) {
      activeProfileEnabled = false;
    } else {
      var p = profilesData.find(function (x) { return x.id === activeProfileId; });
      activeProfileEnabled = !!(p && p.enabled === true);
    }
    refreshControlButtons();
  }

  function refreshControlButtons() {
    if (!lastStatus) {
      btnArm.disabled = true;
      btnDisarm.disabled = true;
      btnEstop.disabled = true;
      btnClearStop.disabled = true;
      return;
    }

    var provider = lastStatus.provider || lastStatus;
    var ctrl = lastStatus.controller || lastStatus;
    var health = provider.health || "Disconnected";
    var state = ctrl.state || ctrl.controllerState || "Disarmed";
    var isStoppedOrFaulted = state === "Stopped" || state === "Faulted";
    var isDisarmed = state === "Disarmed";
    var isArmed = state === "Armed" || state === "Evaluating" || state === "ActionPending"
      || state === "WaitingForPlayer" || state === "WaitingForTarget";
    var armClientReady = !profileSelectionPending
      && !isArmed
      && !isStoppedOrFaulted
      && health === "Healthy"
      && activeProfileEnabled;

    btnArm.disabled = !armClientReady;
    btnDisarm.disabled = isDisarmed || isStoppedOrFaulted;
    btnEstop.disabled = isDisarmed || isStoppedOrFaulted;
    // Faulted is deliberately not clearable. Clear Stop only releases the
    // explicit Stopped latch.
    btnClearStop.disabled = state !== "Stopped";
    updateSettingsButtonState();
    updateEditorSaveState();
  }

  async function loadProfiles() {
    btnReloadProfiles.disabled = true;
    btnReloadProfiles.textContent = "Loading…";
    try {
      var response = await apiFetch(PROFILES_ENDPOINT);
      if (!Array.isArray(response) && !profileSelectionPending) {
        activeProfileId = response.activeProfileId || "";
        selectedProfileId = activeProfileId;
      }
      populateProfileSelect(Array.isArray(response) ? response : (response.profiles || []));
    } catch (err) {
      addLogEntry("error", "profiles", "Failed to load profiles: " + err.message);
      populateProfileSelect([]);
    } finally {
      btnReloadProfiles.textContent = "Reload";
      btnReloadProfiles.disabled = false;
    }
  }

  // ---- Event log ----
  function addLogEntry(level, source, message, timestamp) {
    var entry = {
      level: (level || "info").toLowerCase(),
      source: source || "",
      message: message || "",
      time: timestamp || new Date().toISOString(),
    };
    logEntries.push(entry);
    if (logEntries.length > MAX_LOG_ENTRIES) {
      logEntries = logEntries.slice(-MAX_LOG_ENTRIES);
    }
    renderLogEntry(entry);
  }

  function renderLogEntry(entry) {
    logEmpty.hidden = true;

    var div = document.createElement("div");
    div.className = "log-entry";
    div.setAttribute("data-level", entry.level);

    // Check filter
    var filterVal = logFilter.value;
    if (filterVal !== "all" && entry.level !== filterVal) {
      div.hidden = true;
    }

    var html = '<span class="log-entry__time">' + escapeHtml(formatTime(entry.time)) + '</span>';
    html += '<span class="log-entry__level log-entry__level--' + escapeHtml(entry.level) + '">'
      + escapeHtml(entry.level) + '</span>';
    if (entry.source) {
      html += '<span class="log-entry__source">[' + escapeHtml(entry.source) + ']</span>';
    }
    html += '<span class="log-entry__msg">' + escapeHtml(entry.message) + '</span>';
    div.innerHTML = html;

    logContainer.appendChild(div);

    // Trim DOM nodes
    while (logContainer.children.length > MAX_LOG_ENTRIES + 1) {
      logContainer.removeChild(logContainer.firstElementChild);
    }

    // Auto-scroll
    if (logAutoscroll.checked) {
      logContainer.scrollTop = logContainer.scrollHeight;
    }
  }

  function clearLog() {
    logEntries = [];
    logContainer.innerHTML = "";
    logEmpty.hidden = false;
    logContainer.appendChild(logEmpty);
  }

  function applyLogFilter() {
    var val = logFilter.value;
    var entries = logContainer.querySelectorAll(".log-entry");
    for (var i = 0; i < entries.length; i++) {
      var el = entries[i];
      if (val === "all" || el.getAttribute("data-level") === val) {
        el.hidden = false;
      } else {
        el.hidden = true;
      }
    }
  }

  // ---- SSE ----
  function setSseState(state) {
    sseDot.setAttribute("data-state", state);
    var labels = { connected: "SSE connected", connecting: "SSE connecting…", error: "SSE error", disconnected: "SSE disconnected" };
    sseStatusText.textContent = labels[state] || state;
  }

  function clearSseRetryTimer() {
    if (sseRetryTimer) {
      clearTimeout(sseRetryTimer);
      sseRetryTimer = null;
    }
  }

  function resetSseRetries() {
    clearSseRetryTimer();
    sseRetryCount = 0;
  }

  async function connectSSE() {
    // Do not open SSE when there is no token; fall back to polling only.
    if (!getToken()) {
      setSseState("disconnected");
      return;
    }

    if (sseAbortController) {
      sseAbortController.abort();
    }
    var controller = new AbortController();
    sseAbortController = controller;
    setSseState("connecting");

    var retryError = null;
    var reader = null;
    try {
      var headers = { "Accept": "text/event-stream" };
      var token = getToken();
      if (token) headers["Authorization"] = "Bearer " + token;

      var response = await fetch(SSE_ENDPOINT, {
        headers: headers,
        cache: "no-store",
        signal: controller.signal,
      });
      if (!response.ok || !response.body) {
        throw new Error("HTTP " + response.status + ": " + response.statusText);
      }

      setSseState("connected");
      sseRetryCount = 0;
      reader = response.body.getReader();
      sseReader = reader;
      var decoder = new TextDecoder();
      var pending = "";

      while (!controller.signal.aborted) {
        var result = await reader.read();

        if (result.done) break;
        pending += decoder.decode(result.value, { stream: true }).replace(/\r\n/g, "\n");
        var boundary;
        while ((boundary = pending.indexOf("\n\n")) >= 0) {
          var block = pending.slice(0, boundary);
          pending = pending.slice(boundary + 2);
          var data = block.split("\n")
            .filter(function (line) { return line.startsWith("data:"); })
            .map(function (line) { return line.slice(5).trimStart(); })
            .join("\n");
          if (data) {
            try {
              applyStatus(JSON.parse(data));
            } catch (err) {
              addLogEntry("warn", "sse", "Bad event: " + err.message);
            }
          }
        }
      }

      if (!controller.signal.aborted) throw new Error("Event stream closed");
    } catch (err) {
      if (controller.signal.aborted) return;
      setSseState("error");
      addLogEntry("warn", "sse", "Event stream unavailable: " + err.message);
      retryError = err;
    } finally {
      if (reader) {
        try {
          await reader.cancel();
        } catch {
          // Abort and connection failures can make cancellation reject.
        }
        try {
          reader.releaseLock();
        } catch {
          // The stream may already have released its lock.
        }
      }
      if (sseReader === reader) sseReader = null;
      if (sseAbortController === controller) sseAbortController = null;
    }

    if (retryError && !controller.signal.aborted) scheduleSseRetry();
  }

  function scheduleSseRetry() {
    if (sseRetryTimer) return;
    // Do not schedule retries when there is no token.
    if (!getToken()) return;
    var delay = Math.min(RECONNECT_BASE_MS * Math.pow(2, sseRetryCount), RECONNECT_MAX_MS);
    sseRetryCount++;
    sseRetryTimer = setTimeout(function () {
      sseRetryTimer = null;
      connectSSE();
    }, delay);
  }

  // ---- Status polling (fallback) ----
  async function pollStatus() {
    try {
      var status = await apiFetch(STATUS_ENDPOINT);
      applyStatus(status);
    } catch (err) {
      // SSE should cover this; silent fail on poll
    }
  }

  function startPolling() {
    stopPolling();
    statusPollTimer = setInterval(pollStatus, STATUS_POLL_MS);
  }

  function stopPolling() {
    if (statusPollTimer) {
      clearInterval(statusPollTimer);
      statusPollTimer = null;
    }
  }

  // ---- Settings ----
  async function loadSettings() {
    try {
      var s = await apiFetch("/api/settings");
      if (s) {
        var sc = s.scanner || {};
        setScannerInterval.value = sc.readIntervalMs ?? 50;
        setScannerMaxage.value = sc.localMaxAgeMs ?? 500;
        setScannerProcess.value = sc.processName || "";
        var ev = s.evaluator || {};
        setEvalTelemetryAge.value = ev.maximumTelemetryAgeMs ?? 500;
        setEvalInterval.value = ev.evaluationIntervalMs ?? 100;
        var db = s.dashboard || {};
        setDashInterval.value = db.updateIntervalMs ?? 2000;
        setDashLogLimit.value = db.maxLogEntries ?? 500;
        var lg = s.logging || {};
        setLogRetained.value = lg.retainedFileCountLimit ?? 14;
        settingsStatus.textContent = "";
      }
    } catch (err) {
      addLogEntry("warn", "settings", "Failed to load settings: " + err.message);
    }
  }

  async function saveSettings() {
    var payload = {
      scanner: {
        readIntervalMs: parseInt(setScannerInterval.value) || 50,
        localMaxAgeMs: parseInt(setScannerMaxage.value) || 500,
        processName: setScannerProcess.value.trim() || null,
      },
      evaluator: {
        maximumTelemetryAgeMs: parseInt(setEvalTelemetryAge.value) || 500,
        evaluationIntervalMs: parseInt(setEvalInterval.value) || 100,
      },
      dashboard: {
        updateIntervalMs: parseInt(setDashInterval.value) || 2000,
        maxLogEntries: parseInt(setDashLogLimit.value) || 500,
      },
      logging: {
        retainedFileCountLimit: parseInt(setLogRetained.value) || 14,
      },
    };

    try {
      btnSaveSettings.disabled = true;
      settingsStatus.textContent = "Saving…";
      var result = await apiFetch("/api/settings", "PUT", payload);
      settingsStatus.textContent = "✓ Saved";
      // Refresh form with server state
      await loadSettings();
      addLogEntry("info", "settings", "Settings saved");
    } catch (err) {
      settingsStatus.textContent = "Error: " + err.message;
      addLogEntry("error", "settings", "Settings save failed: " + err.message);
    } finally {
      updateSettingsButtonState();
    }
  }

  function updateSettingsButtonState() {
    if (!lastStatus) {
      btnSaveSettings.disabled = true;
      return;
    }
    var ctrl = lastStatus.controller || lastStatus;
    var state = ctrl.state || ctrl.controllerState || "Disarmed";
    var isDisarmed = state === "Disarmed";
    btnSaveSettings.disabled = !isDisarmed;
  }

  // ---- Control actions ----
  async function doControlAction(endpoint, label) {
    try {
      await apiFetch(endpoint, "POST");
      addLogEntry("info", "control", label + " sent");
      // Poll immediately for fresh state
      pollStatus();
    } catch (err) {
      addLogEntry("error", "control", label + " failed: " + err.message);
    }
  }

  // ---- Arm dialog ----
  async function openArmDialog() {
    // Show loading state
    armReadinessLoading.hidden = false;
    armReadinessResult.hidden = true;
    armReadinessError.hidden = true;
    armDialogConfirm.disabled = true;
    armDialog.showModal();

    try {
      var readiness = await apiFetch("/api/readiness");
      armReadinessLoading.hidden = true;
      armReadinessResult.hidden = false;

      if (readiness.canArm) {
        armReadinessStatus.textContent = "✓ Ready to arm";
        armReadinessStatus.className = "arm-readiness__status arm-readiness__status--ok";
        armDialogConfirm.disabled = false;
      } else {
        armReadinessStatus.textContent = "✗ Cannot arm — " + (readiness.blockers ? readiness.blockers.length : 0) + " blocker(s)";
        armReadinessStatus.className = "arm-readiness__status arm-readiness__status--blocked";
        armDialogConfirm.disabled = true;
      }

      // Render blockers
      armReadinessBlockers.innerHTML = "";
      if (readiness.blockers && readiness.blockers.length > 0) {
        readiness.blockers.forEach(function (b) {
          var li = document.createElement("li");
          li.className = "arm-readiness__blocker";
          li.textContent = "✗ " + b;
          armReadinessBlockers.appendChild(li);
        });
      }

      // Render warnings
      armReadinessWarnings.innerHTML = "";
      if (readiness.warnings && readiness.warnings.length > 0) {
        readiness.warnings.forEach(function (w) {
          var li = document.createElement("li");
          li.className = "arm-readiness__warning";
          li.textContent = "⚠ " + (typeof w === "string" ? w : w.message || "");
          armReadinessWarnings.appendChild(li);
        });
      }
    } catch (err) {
      armReadinessLoading.hidden = true;
      armReadinessError.hidden = false;
      armReadinessError.textContent = "Failed to check readiness: " + err.message;
      armDialogConfirm.disabled = true;
      addLogEntry("warn", "arm", "Readiness check failed: " + err.message);
    }
  }

  // ---- Event wiring ----
  function init() {
    // Token form
    tokenForm.addEventListener("submit", function (e) {
      e.preventDefault();
      setToken(tokenInput.value.trim());
      tokenInput.value = "";
      addLogEntry("info", "auth", "Token updated");
      reconnectAll();
    });

    tokenClear.addEventListener("click", function () {
      setToken("");
      tokenInput.value = "";
      addLogEntry("info", "auth", "Token cleared");
      reconnectAll();
    });

    // Pre-fill if exists
    if (getToken()) {
      tokenInput.placeholder = "•••••••• (token set)";
    }

    // Arm dialog
    btnArm.addEventListener("click", openArmDialog);

    armDialogCancel.addEventListener("click", function () {
      armDialog.close();
    });

    armDialogForm.addEventListener("submit", async function (e) {
      e.preventDefault();
      // Re-validate readiness at submit time
      try {
        var readiness = await apiFetch("/api/readiness");
        if (!readiness.canArm) {
          armReadinessStatus.textContent = "✗ Readiness changed — cannot arm";
          armReadinessStatus.className = "arm-readiness__status arm-readiness__status--blocked";
          armDialogConfirm.disabled = true;
          addLogEntry("warn", "arm", "Readiness check failed at submit: " + (readiness.blockers ? readiness.blockers.join("; ") : ""));
          return;
        }
      } catch (err) {
        addLogEntry("error", "arm", "Readiness re-check failed: " + err.message);
        return;
      }
      armDialog.close();
      doControlAction(ARM_ENDPOINT, "Arm");
    });

    // Disarm & E-Stop
    btnDisarm.addEventListener("click", function () {
      doControlAction(DISARM_ENDPOINT, "Disarm");
    });

    btnEstop.addEventListener("click", function () {
      if (confirm("Emergency stop will immediately halt all bot actions. Continue?")) {
        doControlAction(ESTOP_ENDPOINT, "Emergency Stop");
      }
    });

    // Clear Stop
    btnClearStop.addEventListener("click", function () {
      doControlAction(CLEAR_STOP_ENDPOINT, "Clear Stop");
    });

    // Profiles
    btnReloadProfiles.addEventListener("click", function () {
      addLogEntry("info", "profiles", "Reloading profiles…");
      apiFetch(RELOAD_PROFILES_ENDPOINT, "POST")
        .then(function () {
          addLogEntry("info", "profiles", "Reload triggered");
          return loadProfiles();
        })
        .catch(function (err) {
          addLogEntry("error", "profiles", "Reload failed: " + err.message);
        });
    });

    profileSelect.addEventListener("change", function () {
      var requestedProfileId = profileSelect.value;
      var previousActiveProfileId = activeProfileId;
      if (!requestedProfileId) {
        selectedProfileId = previousActiveProfileId;
        profileSelect.value = previousActiveProfileId;
        showProfileDetail(previousActiveProfileId);
        updateActiveProfileEnabled();
        return;
      }

      selectedProfileId = requestedProfileId;
      showProfileDetail(requestedProfileId);
      profileSelectionPending = true;
      activeProfileId = "";
      profileSelect.disabled = true;
      updateActiveProfileEnabled();
      // POST active profile selection
      apiFetch("/api/control/profile", "POST", { profileId: requestedProfileId })
        .then(function () {
          profileSelectionPending = false;
          activeProfileId = requestedProfileId;
          updateActiveProfileEnabled();
          addLogEntry("info", "profiles", "Active profile: " + requestedProfileId);
        })
        .catch(function (err) {
          profileSelectionPending = false;
          activeProfileId = previousActiveProfileId;
          selectedProfileId = previousActiveProfileId;
          profileSelect.value = previousActiveProfileId;
          showProfileDetail(previousActiveProfileId);
          updateActiveProfileEnabled();
          addLogEntry("warn", "profiles", "Profile select failed: " + err.message);
        })
        .finally(function () {
          profileSelect.disabled = profilesData.length === 0;
        });
    });

    // Log controls
    logFilter.addEventListener("change", applyLogFilter);
    btnClearLog.addEventListener("click", clearLog);

    // Settings form
    settingsForm.addEventListener("submit", function (e) {
      e.preventDefault();
      saveSettings();
    });

    // Profile editor
    btnEditProfile.addEventListener("click", openProfileEditor);
    btnEditorRefresh.addEventListener("click", function () {
      if (selectedProfileId) openProfileEditor();
    });
    btnEditorClose.addEventListener("click", closeProfileEditor);
    btnSaveProfile.addEventListener("click", saveProfileEdit);

    // Keyboard: Escape closes dialog
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && armDialog.open) {
        armDialog.close();
      }
    });

    // Initial load
    addLogEntry("info", "system", "Dashboard loaded");
    if (getToken()) loadSettings();
    reconnectAll();
  }

  // ---- Profile editor ----
  async function openProfileEditor() {
    if (!selectedProfileId) return;
    try {
      editorStatus.textContent = "Loading…";
      profileEditorSection.hidden = false;
      btnSaveProfile.disabled = true;
      var profile = await apiFetch("/api/profiles/" + encodeURIComponent(selectedProfileId));
      profileEditorTextarea.value = JSON.stringify(profile, null, 2);
      editorFilename.textContent = selectedProfileId + ".json";
      editorStatus.textContent = "";
      updateEditorSaveState();
    } catch (err) {
      editorStatus.textContent = "Error: " + err.message;
      addLogEntry("error", "editor", "Failed to load profile: " + err.message);
    }
  }

  function closeProfileEditor() {
    profileEditorSection.hidden = true;
    editorStatus.textContent = "";
  }

  async function saveProfileEdit() {
    if (!selectedProfileId) return;
    var json = profileEditorTextarea.value;
    var body;
    try {
      body = JSON.parse(json);
    } catch (e) {
      editorStatus.textContent = "Invalid JSON: " + e.message;
      return;
    }

    try {
      btnSaveProfile.disabled = true;
      editorStatus.textContent = "Saving…";
      await apiFetch("/api/profiles/" + encodeURIComponent(selectedProfileId), "PUT", body);
      editorStatus.textContent = "✓ Saved";
      addLogEntry("info", "editor", "Profile '" + selectedProfileId + "' saved");
      // Reload profiles to refresh cache
      await loadProfiles();
    } catch (err) {
      editorStatus.textContent = "Error: " + err.message;
      addLogEntry("error", "editor", "Profile save failed: " + err.message);
    } finally {
      updateEditorSaveState();
    }
  }

  function updateEditorSaveState() {
    if (!lastStatus) {
      btnSaveProfile.disabled = true;
      return;
    }
    var ctrl = lastStatus.controller || lastStatus;
    var state = ctrl.state || ctrl.controllerState || "Disarmed";
    btnSaveProfile.disabled = state !== "Disarmed";
  }

  function reconnectAll() {
    // Clear any pending retry/keepalive timers to prevent accumulation.
    resetSseRetries();
    if (sseAbortController) {
      sseAbortController.abort();
      sseAbortController = null;
    }
    if (sseReader) {
      sseReader.cancel().catch(function () { });
    }
    if (getToken()) loadSettings();
    loadProfiles();
    connectSSE();
    startPolling();
    pollStatus();
  }

  // ---- Boot ----
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
