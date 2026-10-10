// ZCode7 chat timeline renderer (WebView2 side)
"use strict";

var list = document.getElementById("list");
var rows = {};          // id -> element
var mdReady = false;

function esc(s) {
  return String(s == null ? "" : s)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function md(text) {
  try {
    return DOMPurify.sanitize(marked.parse(text == null ? "" : text, { breaks: true }));
  } catch (e) { return esc(text); }
}

// tiny inline svg icons (lucide shapes, stroke=currentColor)
function icon(name) {
  var p = {
    terminal: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="m8 9 3 3-3 3M13 15h3"/>',
    file: '<path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/>',
    pencil: '<path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/>',
    search: '<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>',
    globe: '<circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20"/>',
    sparkles: '<path d="m12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3Z"/>',
    listtree: '<path d="M21 8h-8M21 16h-8M9 4l-5 5 5 5"/>' ,
    wrench: '<path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/>',
    brain: '<path d="M12 5a3 3 0 1 0-5.997.125 4 4 0 0 0-2.526 5.77 4 4 0 0 0 .556 6.588A4 4 0 1 0 12 18Z"/><path d="M12 5a3 3 0 1 1 5.997.125 4 4 0 0 1 2.526 5.77 4 4 0 0 1-.556 6.588A4 4 0 1 1 12 18Z"/>',
    clock: '<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>',
    chev: '<path d="m9 18 6-6-6-6"/>',
    check: '<path d="M21.801 10A10 10 0 1 1 17 3.335"/><path d="m9 11 3 3L22 4"/>',
    x: '<path d="M18 6 6 18M6 6l12 12"/>',
    shield: '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/>'
  }[name] || "";
  return '<svg class="icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="color:rgba(38,38,38,.4)">' + p + "</svg>";
}

function el(tag, cls, html) {
  var e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  return e;
}

function kindMeta(tool) {
  var map = {
    Bash: ["SquareTerminal", "终端"], Read: ["File", "读取"], Write: ["Pencil", "写入"],
    Edit: ["Pencil", "编辑"], Glob: ["Search", "搜索"], Grep: ["Search", "搜索"],
    WebFetch: ["Globe", "查阅"], Skill: ["Sparkles", "技能"], Todo: ["ListTree", "计划"],
    SystemInfo: ["Wrench", "系统"], Mcp: ["Wrench", "工具"]
  };
  return map[tool] || ["Wrench", tool];
}

function argsPreview(tool, args) {
  try {
    var d = JSON.parse(args);
    if (tool === "Bash") return d.command || "";
    if (tool === "Todo") {
      var n = (d.todos || []).length;
      return n + " 项任务";
    }
    return d.path || d.file_path || d.pattern || d.url || d.name || args;
  } catch (e) { return args; }
}

function bottom() {
  requestAnimationFrame(function () { window.scrollTo(0, document.body.scrollHeight); });
}

window.chat = {
  ready: function () { window.chrome.webview.postMessage({ type: "ready" }); },

  add: function (id, kind) {
    var e;
    if (kind === "user") e = el("div", "row user", '<div class="bubble"></div>');
    else if (kind === "assistant") e = el("div", "row assistant", '<div class="md"></div>');
    else if (kind === "tool") {
      e = el("div", "row toolwrap",
        '<div class="toolrow"><span class="icon"></span><span class="kind"></span>' +
        '<span class="args"></span><span class="plus"></span><span class="minus"></span>' +
        '<span class="chev">' + icon("chev") + "</span></div>" +
        '<div class="toolcard"></div>');
    } else if (kind === "thinking") {
      e = el("div", "row thinkwrap",
        '<div class="thinkrow"><span class="icon">' + icon("brain") + '</span>' +
        '<span class="label">思考</span><span class="sep">·</span><span class="dur"></span></div>' +
        '<div class="thinkbody"></div>');
    } else if (kind === "work") {
      e = el("div", "row workwrap",
        '<div class="workrow"><span class="txt"></span></div>' +
        '<div style="border-bottom:1px solid rgba(13,13,13,.05);margin-top:8px"></div>');
    } else if (kind === "approval") {
      e = el("div", "row approvalwrap");
    } else e = el("div", "row info", '<span class="info"></span>');

    e.dataset.id = id;
    rows[id] = e;
    list.appendChild(e);
    bottom();
  },

  user: function (id, text) {
    var e = rows[id]; if (!e) return;
    e.querySelector(".bubble").textContent = text;
    bottom();
  },

  assistant: function (id, text) {
    var e = rows[id]; if (!e) return;
    e.querySelector(".md").innerHTML = DOMPurify.sanitize(
      marked.parse(text || "", { breaks: true, mangle: false, headerIds: false }));
    bottom();
  },

  info: function (id, text) {
    var e = rows[id]; if (!e) return;
    e.querySelector(".info").textContent = text;
  },

  tool: function (id, tool, args, result, running, addLines) {
    var e = rows[id]; if (!e) return;
    var meta = kindMeta(tool);
    e.querySelector(".icon").innerHTML = icon(meta[0]);
    e.querySelector(".kind").textContent = meta[1];
    e.querySelector(".args").textContent = argsPreview(tool, args);
    var plus = e.querySelector(".plus"), minus = e.querySelector(".minus");
    plus.textContent = addLines > 0 ? "+" + addLines : "";
    minus.textContent = "";
    var card = e.querySelector(".toolcard");
    var row = e.querySelector(".toolrow");
    var label = running ? "" : (result ? "· " + String(result).split("\n")[0] : "");
    // result first-line as suffix
    e.querySelector(".args").textContent = argsPreview(tool, args) + (label ? "  ·  " + label.replace(/^· /, "") : "");
    if (!running && result) {
      card.textContent = "$ " + args + "\n" + result;
    }
    row.classList.toggle("open", !!e.dataset.open);
    bottom();
  },

  thinking: function (id, text, dur, running) {
    var e = rows[id]; if (!e) return;
    e.querySelector(".dur").textContent = running ? "正在思考" : (dur ? "持续了 " + dur : "");
    if (text) e.querySelector(".thinkbody").textContent = text;
    bottom();
  },

  toolresult: function (id, result) {
    var e = rows[id]; if (!e) return;
    var row = e.querySelector('.toolrow');
    var card = e.querySelector('.toolcard');
    if (card) card.textContent = (card.textContent ? card.textContent + "\n" : "") + result;
    if (row && !e.dataset.open) { /* keep collapsed */ }
    bottom();
  },

  work: function (id, dur, running) {
    var e = rows[id]; if (!e) return;
    e.querySelector(".txt").textContent = (running ? "工作中" : "工作了") + (dur ? " " + dur : "");
  },

  approval: function (id, tool, args, decided, allowed) {
    var e = rows[id]; if (!e) return;
    var meta = kindMeta(tool);
    var html = '<div class="approval">' +
      '<div class="head"><span class="ik">' + icon(meta[0]) + '</span>' +
      '<span class="kind">' + esc(meta[1]) + '</span>' +
      '<span class="args">' + esc(args) + '</span></div>';
    if (!decided) {
      html += '<div class="btns">' +
        '<button class="allow" data-a="allow">允许执行</button>' +
        '<button data-a="deny">拒绝</button></div>';
    } else {
      html += '<div class="state ' + (allowed ? "ok" : "no") + '">' +
        (allowed ? "✓ 已允许执行" : "✗ 已拒绝") + "</div>";
    }
    html += "</div>";
    e.innerHTML = html;
    if (!decided) {
      e.querySelectorAll("button").forEach(function (b) {
        b.addEventListener("click", function () {
          window.chrome.webview.postMessage({ type: "approval", row: id, action: b.dataset.a });
        });
      });
    }
    bottom();
  },

  clear: function () { list.innerHTML = ""; rows = {}; },

  bottom: function () { bottom(); },

  todos: function (items) { /* status panel is WinForms-side; reserved */ }
};

// toolrow click toggles the detail card
list.addEventListener("click", function (ev) {
  var row = ev.target.closest(".toolrow");
  if (row) {
    var wrap = row.parentElement;
    wrap.dataset.open = wrap.dataset.open === "1" ? "0" : "1";
    row.classList.toggle("open", wrap.dataset.open === "1");
    var card = wrap.querySelector(".toolcard");
    if (card) card.style.display = wrap.dataset.open === "1" ? "block" : "none";
  }
});

window.addEventListener("error", function (ev) {
  try { window.chrome.webview.postMessage({ type: "jserror", msg: String(ev.message) }); } catch (e) {}
});
window.chat.ready();
