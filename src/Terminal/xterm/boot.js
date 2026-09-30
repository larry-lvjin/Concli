'use strict';
(function () {
  // Startup palette only. The authority is TerminalTheme.cs on the host side, which
  // pushes a full theme through ptyHost.setTheme whenever the app theme changes —
  // this copy just has to match index.html so the first frame is not the wrong colour.
  //
  // Everything else mirrors Windows Terminal's defaults for a PowerShell profile
  // (Cascadia Mono 12pt, bar cursor, 9001 lines of scrollback) — the pane is supposed
  // to be indistinguishable from PowerShell itself.
  var term = new Terminal({
    fontFamily: '"Cascadia Mono", Consolas, "Courier New", monospace',
    fontSize: 16,               // 12pt
    lineHeight: 1.0,
    letterSpacing: 0,
    cursorBlink: true,
    cursorStyle: 'bar',
    scrollback: 9001,
    allowProposedApi: true,
    theme: {
      background:    '#0C0C0C',
      foreground:    '#CCCCCC',
      cursor:        '#FFFFFF',
      cursorAccent:  '#0C0C0C',
      selectionBackground: '#FFFFFF66'
    }
  });

  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('term'));

  var host = window.chrome && window.chrome.webview;
  function send(msg) { if (host) host.postMessage(msg); }

  // ── keystrokes / pasted text -> ConPTY stdin ──────────────
  term.onData(function (d) { send({ type: 'input', data: d }); });

  // ── clipboard, the Windows Terminal way ───────────────────
  // Ctrl+C copies when something is selected and is ^C otherwise; Ctrl+V and
  // Ctrl+Shift+C/V are copy/paste; right-click copies a selection or pastes.
  // The clipboard itself is the host's: the page has no clipboard permission.
  function copySelection() {
    if (!term.hasSelection()) return false;
    send({ type: 'copy', data: term.getSelection() });
    term.clearSelection();
    return true;
  }
  function requestPaste() { send({ type: 'paste' }); }

  term.attachCustomKeyEventHandler(function (e) {
    if (e.type !== 'keydown' || !e.ctrlKey || e.altKey) return true;
    var k = e.key.toLowerCase();
    if (k === 'c' && (e.shiftKey || term.hasSelection())) {
      copySelection(); e.preventDefault(); return false;
    }
    if (k === 'v') { requestPaste(); e.preventDefault(); return false; }
    return true;
  });

  document.addEventListener('contextmenu', function (e) {
    e.preventDefault();
    if (!copySelection()) requestPaste();
  });

  // ── IME anchor ────────────────────────────────────────────
  // xterm parks its hidden textarea (and so the IME candidate window) on the real
  // cursor. TUIs like Claude Code hide that cursor, leave it at the end of their last
  // frame, and draw their own caret as a reverse-video cell — so the candidate window
  // lands at the far right of the status line. While the cursor is hidden, anchor on
  // the bottom-most single reverse-video cell instead. Private API: re-check on upgrade.
  var core = term._core;
  var probe = null;
  function fakeCaret() {
    if (!core || !core.coreService || !core.coreService.isCursorHidden) return null;
    var b = term.buffer.active;
    for (var r = term.rows - 1; r >= 0; r--) {
      var line = b.getLine(b.viewportY + r);
      if (!line) continue;
      for (var c = 0; c < term.cols; c++) {
        probe = line.getCell(c, probe);
        if (!probe || !probe.isInverse()) continue;
        // A caret is one glyph wide: one cell, or a wide char plus its spacer.
        var w = Math.max(probe.getWidth(), 1);
        var next = line.getCell(c + w);
        if (!next || !next.isInverse()) return { x: c, y: r };
        while (c + 1 < term.cols && (next = line.getCell(c + 1)) && next.isInverse()) c++;
      }
    }
    return null;
  }
  function moveTo(el, pos) {
    var cell = core._renderService.dimensions.css.cell;
    el.style.left = (pos.x * cell.width) + 'px';
    el.style.top  = (pos.y * cell.height) + 'px';
  }
  if (core && core._syncTextArea && core._compositionHelper) {
    var sync = core._syncTextArea.bind(core);
    core._syncTextArea = function () {
      sync();
      if (core._compositionHelper.isComposing) return;
      var p = fakeCaret();
      if (p && core.textarea) moveTo(core.textarea, p);
    };
    var ch = core._compositionHelper;
    var update = ch.updateCompositionElements.bind(ch);
    ch.updateCompositionElements = function (dontRecurse) {
      update(dontRecurse);
      if (!ch._isComposing) return;
      var p = fakeCaret();
      if (p) { moveTo(ch._compositionView, p); moveTo(ch._textarea, p); }
    };
  }

  // Report the OSC 0/2 title so the tab can name itself.
  term.onTitleChange(function (t) { send({ type: 'title', data: t }); });

  // ── size negotiation ──────────────────────────────────────
  // xterm computes rows/cols from pixel size; ConPTY must be told, or output wraps wrong.
  var lastCols = 0, lastRows = 0;
  function pushSize() {
    try { fit.fit(); } catch (e) { return; }
    if (term.cols !== lastCols || term.rows !== lastRows) {
      lastCols = term.cols; lastRows = term.rows;
      send({ type: 'resize', cols: term.cols, rows: term.rows });
    }
  }

  var resizeTimer = null;
  new ResizeObserver(function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(pushSize, 50);   // debounce: dragging fires continuously
  }).observe(document.getElementById('term'));

  // ── ConPTY stdout -> xterm ────────────────────────────────
  // Host sends pre-batched chunks as raw strings on the hot path.
  if (host) {
    host.addEventListener('message', function (e) {
      if (typeof e.data === 'string') term.write(e.data);
    });
  }

  // ── control surface for the host (rare, called via ExecuteScriptAsync) ──
  window.ptyHost = {
    focus:  function () { term.focus(); },
    clear:  function () { term.clear(); },
    fit:    pushSize,
    size:   function () { return { cols: term.cols, rows: term.rows }; },
    setFontSize: function (px) { term.options.fontSize = px; pushSize(); },

    // Host-driven theming. The scrollbar and the area outside the grid are plain CSS,
    // so they have to be repainted alongside xterm's own canvas.
    setTheme: function (t) {
      term.options.theme = t;
      document.documentElement.style.background = t.background;
      document.body.style.background = t.background;
      document.documentElement.style.setProperty('--scroll-track', t.background);
      document.documentElement.style.setProperty('--scroll-thumb', t.brightBlack || '#767676');
    },

    // Clipboard text from the host. term.paste honours bracketed-paste mode, so
    // PSReadLine gets a multi-line paste as one edit instead of running each line.
    paste:  function (s) { term.paste(s); term.focus(); },

    // Feed text to the shell as if typed — used to inject `claude --resume <id>`.
    type:   function (s) { send({ type: 'input', data: s }); },

    // Rendered screen + scrollback as plain text. Unlike the raw tee this is
    // already VT-resolved, so it is what "copy all" should hand out.
    dump:   function () {
      var b = term.buffer.active, out = [];
      for (var i = 0; i < b.length; i++) {
        var line = b.getLine(i);
        out.push(line ? line.translateToString(true) : '');
      }
      return out.join('\n');
    }
  };

  pushSize();
  term.focus();
  send({ type: 'ready', cols: term.cols, rows: term.rows });
})();
