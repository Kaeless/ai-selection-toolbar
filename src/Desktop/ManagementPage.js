"use strict";
// The launch URL carries a random per-process token in the fragment, which is never sent in HTTP.
// Remove the fragment immediately and keep the token only for this tab's lifetime.
const token = location.hash.slice(1);
history.replaceState(null, "", location.pathname);
const byId = id => document.getElementById(id);
const status = message => { byId("status").textContent = message; };

async function api(path, method = "GET", body) {
  if (!token) throw new Error("请从桌面浮窗的“管理”按钮打开此页面");
  const response = await fetch(path, {
    method,
    headers: {"Authorization": "Bearer " + token, ...(body ? {"Content-Type": "application/json"} : {})},
    body: body ? JSON.stringify(body) : undefined,
    cache: "no-store",
    credentials: "omit"
  });
  if (!response.ok) throw new Error((await response.text()) || "请求失败（" + response.status + "）");
  return response.json();
}

async function loadSettings() {
  const value = await api("/api/settings");
  byId("autoShow").checked = value.AutoShow;
  byId("startOnLogin").checked = !!value.StartOnLogin;
  byId("language").value = value.TargetLanguage || "";
  byId("notesDirectory").value = value.NotesDirectory || "";
  byId("apiBaseUrl").value = value.ApiBaseUrl || "";
  byId("model").value = value.Model || "";
  byId("apiKey").value = "";
  const list = byId("excludedList");
  list.replaceChildren();
  for (const name of value.ExcludedApplications || []) {
    const row = document.createElement("li"); row.className = "row";
    const label = document.createElement("span"); label.textContent = name;
    const remove = document.createElement("button"); remove.textContent = "移除";
    remove.addEventListener("click", () => run(async () => {
      await api("/api/exclusions/remove", "POST", {application: name}); await loadSettings(); status("已移除排除程序");
    }));
    row.append(label, remove); list.append(row);
  }
}

let historyOffset = 0;
let historyRequest = 0;
async function loadHistory() {
  const request = ++historyRequest;
  const offset = historyOffset;
  const query = byId("historySearch").value.trim();
  const items = await api("/api/history?search=" + encodeURIComponent(query) + "&offset=" + offset);
  if (request !== historyRequest) return;
  const list = byId("historyList"); list.replaceChildren();
  byId("historyPage").textContent = "第 " + (Math.floor(offset / 50) + 1) + " 页";
  byId("historyPrev").disabled = offset === 0;
  byId("historyNext").disabled = items.length < 50;
  if (!items.length) { list.textContent = "没有匹配的历史记录"; return; }
  for (const item of items) {
    const card = document.createElement("article"); card.className = "entry";
    const meta = document.createElement("div"); meta.className = "meta";
    meta.textContent = new Date(Number(item.TimeUtc.match(/\d+/)?.[0] || 0)).toLocaleString() + " · " + item.Action + " · " + item.Application;
    const selection = document.createElement("div"); selection.className = "selection"; selection.textContent = item.Selection;
    card.append(meta, selection);
    if (item.Prompt) { const question = document.createElement("div"); question.textContent = "提问：" + item.Prompt; card.append(question); }
    const result = document.createElement("div"); result.textContent = item.Result;
    const remove = document.createElement("button"); remove.textContent = "删除";
    remove.addEventListener("click", () => run(async () => {
      if (!confirm("删除这条历史记录？")) return;
      await api("/api/history/delete", "POST", {id: item.Id});
      await loadHistory(); status("已删除历史记录");
    }));
    card.append(result, remove); list.append(card);
  }
}

async function run(fn) { try { await fn(); } catch (error) { status(error.message); } }
byId("saveSettings").addEventListener("click", () => run(async () => {
  await api("/api/settings", "POST", {AutoShow: byId("autoShow").checked,
    StartOnLogin: byId("startOnLogin").checked,
    TargetLanguage: byId("language").value.trim(),
    NotesDirectory: byId("notesDirectory").value.trim()});
  status("设置已保存");
}));
byId("saveConnection").addEventListener("click", () => run(async () => {
  await api("/api/connection", "POST", {ApiBaseUrl: byId("apiBaseUrl").value.trim(),
    Model: byId("model").value.trim(), ApiKey: byId("apiKey").value});
  await loadSettings();
  status("连接配置已保存，当前模型：" + byId("model").value);
}));
byId("addExclusion").addEventListener("click", () => run(async () => {
  const name = byId("excludedInput").value.trim();
  await api("/api/exclusions/add", "POST", {application: name});
  byId("excludedInput").value = ""; await loadSettings(); status("已添加排除程序");
}));
byId("historySearch").addEventListener("input", () => { historyOffset = 0; run(loadHistory); });
byId("historyPrev").addEventListener("click", () => { historyOffset = Math.max(0, historyOffset - 50); run(loadHistory); });
byId("historyNext").addEventListener("click", () => { historyOffset += 50; run(loadHistory); });
byId("clearHistory").addEventListener("click", () => run(async () => {
  if (!confirm("确定清空全部历史记录？此操作无法撤销。")) return;
  await api("/api/history/clear", "POST", {});
  historyOffset = 0;
  await loadHistory(); status("历史记录已清空");
}));
byId("settingsTab").addEventListener("click", () => {
  byId("settingsPanel").hidden = false; byId("historyPanel").hidden = true;
  byId("settingsTab").className = "selected"; byId("historyTab").className = "";
  run(loadSettings);
});
byId("historyTab").addEventListener("click", () => {
  byId("settingsPanel").hidden = true; byId("historyPanel").hidden = false;
  byId("settingsTab").className = ""; byId("historyTab").className = "selected";
  run(loadHistory);
});
run(loadSettings);
