"use strict";
// The launch fragment is never sent over HTTP. Keep the token only in this tab.
const token = location.hash.slice(1);
history.replaceState(null, "", location.pathname);
const byId = id => document.getElementById(id);
const status = message => { byId("status").textContent = message; };
let customActions = [];
let historyOffset = 0;
let historyRequest = 0;

async function api(path, method = "GET", body) {
  if (!token) throw new Error("请从桌面工具栏打开管理页");
  const response = await fetch(path, {method,
    headers: {"Authorization": "Bearer " + token, ...(body ? {"Content-Type": "application/json"} : {})},
    body: body ? JSON.stringify(body) : undefined, cache: "no-store", credentials: "omit"});
  if (!response.ok) throw new Error((await response.text()) || "请求失败（" + response.status + "）");
  return response.json();
}
async function run(fn) { try { await fn(); } catch (error) { status(error.message); } }

const sections = {
  settings: ["settingsTab", "settingsPanel", "常规设置", "让划词后的下一步更顺手。"],
  toolbar: ["toolbarTab", "toolbarPanel", "工具栏", "让常用操作更贴近你的工作方式。"],
  history: ["historyTab", "historyPanel", "历史记录", "回看之前处理过的文字。"],
  exclusions: ["exclusionsTab", "exclusionsPanel", "排除程序", "选择不需要弹出工具栏的程序。"]
};
function showSection(name) {
  for (const [key, [tab, panel]] of Object.entries(sections)) {
    byId(tab).classList.toggle("selected", key === name);
    byId(panel).hidden = key !== name;
  }
  byId("pageTitle").textContent = sections[name][2];
  byId("pageSubtitle").textContent = sections[name][3];
  status("");
  if (name === "history") run(loadHistory);
  if (name === "exclusions") run(loadExclusions);
}

function renderExclusions(names) {
  const list = byId("excludedList"); list.replaceChildren();
  if (!names.length) { const empty = document.createElement("li"); empty.className = "empty"; empty.textContent = "暂无排除程序"; list.append(empty); return; }
  for (const name of names) {
    const row = document.createElement("li"); row.className = "row";
    const label = document.createElement("span"); label.textContent = name;
    const remove = document.createElement("button"); remove.textContent = "移除"; remove.type = "button";
    remove.addEventListener("click", () => run(async () => {
      await api("/api/exclusions/remove", "POST", {application: name});
      await loadExclusions(); status("已移除 " + name);
    }));
    row.append(label, remove); list.append(row);
  }
}
async function loadExclusions() { renderExclusions((await api("/api/settings")).ExcludedApplications || []); }

function updatePreview() {
  const color = byId("toolbarColor").value.toUpperCase();
  byId("colorValue").textContent = color;
  document.documentElement.style.setProperty("--accent", color);
  const preview = byId("toolbarPreview");
  preview.classList.toggle("compact", byId("toolbarStyle").value === "compact");
  while (preview.children.length > 5) preview.lastElementChild.remove();
  for (const item of customActions) {
    const chip = document.createElement("span"); chip.textContent = item.Name || "新按钮"; preview.append(chip);
  }
}

function renderCustomActions() {
  const list = byId("customActionList"); list.replaceChildren();
  if (!customActions.length) {
    const empty = document.createElement("p"); empty.className = "empty";
    empty.textContent = "还没有自定义按钮。可以添加“总结”“改写”等常用操作。"; list.append(empty);
  }
  customActions.forEach((item, index) => {
    const row = document.createElement("div"); row.className = "custom-row";
    const head = document.createElement("div"); head.className = "custom-row-head";
    const heading = document.createElement("strong"); heading.textContent = "按钮 " + (index + 1);
    const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "删除";
    remove.addEventListener("click", () => { customActions.splice(index, 1); renderCustomActions(); });
    head.append(heading, remove);
    const nameField = document.createElement("div"); nameField.className = "field";
    const nameLabel = document.createElement("label"); nameLabel.textContent = "按钮名称";
    const name = document.createElement("input"); name.maxLength = 20; name.placeholder = "例如：总结"; name.value = item.Name || "";
    name.addEventListener("input", () => { item.Name = name.value; updatePreview(); });
    nameField.append(nameLabel, name);
    const promptField = document.createElement("div"); promptField.className = "field";
    const promptLabel = document.createElement("label"); promptLabel.textContent = "提示词";
    const prompt = document.createElement("textarea"); prompt.maxLength = 4000; prompt.placeholder = "描述如何处理所选文字"; prompt.value = item.Prompt || "";
    prompt.addEventListener("input", () => { item.Prompt = prompt.value; });
    promptField.append(promptLabel, prompt);
    row.append(head, nameField, promptField); list.append(row);
  });
  byId("addCustomAction").disabled = customActions.length >= 8;
  updatePreview();
}

async function loadSettings() {
  const value = await api("/api/settings");
  byId("autoShow").checked = !!value.AutoShow;
  byId("startOnLogin").checked = !!value.StartOnLogin;
  byId("language").value = value.TargetLanguage || "";
  byId("notesDirectory").value = value.NotesDirectory || "";
  byId("apiBaseUrl").value = value.ApiBaseUrl || "";
  byId("model").value = value.Model || "";
  byId("apiKey").value = "";
  byId("toolbarStyle").value = value.ToolbarStyle === "compact" ? "compact" : "standard";
  byId("toolbarColor").value = /^#[0-9a-f]{6}$/i.test(value.ToolbarAccentColor || "") ? value.ToolbarAccentColor : "#4F46E5";
  customActions = (value.CustomActions || []).map(x => ({Id: x.Id || "", Name: x.Name || "", Prompt: x.Prompt || ""}));
  renderCustomActions(); renderExclusions(value.ExcludedApplications || []);
}

async function saveSettings() {
  if (customActions.length > 8 || customActions.some(x => !x.Name.trim() || !x.Prompt.trim()))
    throw new Error("每个自定义按钮都需要名称和提示词（最多 8 个）");
  await api("/api/settings", "POST", {
    AutoShow: byId("autoShow").checked, StartOnLogin: byId("startOnLogin").checked,
    TargetLanguage: byId("language").value.trim(), NotesDirectory: byId("notesDirectory").value.trim(),
    ToolbarStyle: byId("toolbarStyle").value, ToolbarAccentColor: byId("toolbarColor").value.toUpperCase(),
    CustomActions: customActions.map(x => ({Id: x.Id, Name: x.Name.trim(), Prompt: x.Prompt.trim()}))
  });
  await loadSettings(); // Retrieve generated IDs for new buttons.
  status("设置已保存，工具栏会在下次显示时更新");
}

async function loadHistory() {
  const request = ++historyRequest, offset = historyOffset;
  const query = byId("historySearch").value.trim();
  const items = await api("/api/history?search=" + encodeURIComponent(query) + "&offset=" + offset);
  if (request !== historyRequest) return;
  const list = byId("historyList"); list.replaceChildren();
  byId("historyPage").textContent = "第 " + (Math.floor(offset / 50) + 1) + " 页";
  byId("historyPrev").disabled = offset === 0;
  byId("historyNext").disabled = items.length < 50;
  if (!items.length) { const empty = document.createElement("div"); empty.className = "card empty"; empty.textContent = "没有匹配的历史记录"; list.append(empty); return; }
  for (const item of items) {
    const card = document.createElement("article"); card.className = "entry";
    const meta = document.createElement("div"); meta.className = "meta";
    const dateValue = typeof item.TimeUtc === "string" ? Number(item.TimeUtc.match(/\d+/)?.[0] || 0) : item.TimeUtc;
    const configuredAction = (item.Action || "").startsWith("custom:")
      ? customActions.find(x => x.Id === item.Action.slice(7)) : null;
    const actionName = (item.Action || "").startsWith("custom:")
      ? "自定义 · " + (configuredAction?.Name || "操作") : (item.Action || "操作");
    meta.textContent = new Date(dateValue).toLocaleString() + "  ·  " + actionName + "  ·  " + (item.Application || "未知程序");
    const selection = document.createElement("div"); selection.className = "selection"; selection.textContent = item.Selection || "";
    card.append(meta, selection);
    if (item.Prompt) { const question = document.createElement("div"); question.className = "question"; question.textContent = "提问：" + item.Prompt; card.append(question); }
    const result = document.createElement("div"); result.className = "result"; result.textContent = item.Result || "";
    const footer = document.createElement("div"); footer.className = "entry-footer";
    const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "删除记录";
    remove.addEventListener("click", () => run(async () => {
      if (!confirm("删除这条历史记录？")) return;
      await api("/api/history/delete", "POST", {id: item.Id}); await loadHistory(); status("已删除历史记录");
    }));
    footer.append(remove); card.append(result, footer); list.append(card);
  }
}

for (const [name, [tab]] of Object.entries(sections)) byId(tab).addEventListener("click", () => showSection(name));
byId("saveSettings").addEventListener("click", () => run(saveSettings));
byId("saveToolbar").addEventListener("click", () => run(saveSettings));
byId("toolbarStyle").addEventListener("change", updatePreview);
byId("toolbarColor").addEventListener("input", updatePreview);
byId("addCustomAction").addEventListener("click", () => {
  if (customActions.length >= 8) return;
  customActions.push({Id: "", Name: "", Prompt: ""}); renderCustomActions();
});
byId("saveConnection").addEventListener("click", () => run(async () => {
  await api("/api/connection", "POST", {ApiBaseUrl: byId("apiBaseUrl").value.trim(),
    Model: byId("model").value.trim(), ApiKey: byId("apiKey").value});
  byId("apiKey").value = ""; status("连接配置已保存");
}));
byId("chooseExe").addEventListener("click", () => byId("exeFile").click());
byId("exeFile").addEventListener("change", event => run(async () => {
  const file = event.target.files?.[0];
  if (!file) return;
  try {
    if (!/\.exe$/i.test(file.name)) throw new Error("请选择 .exe 程序文件");
    await api("/api/exclusions/add", "POST", {application: file.name});
    await loadExclusions(); status("已排除 " + file.name);
  } finally { event.target.value = ""; }
}));
byId("historySearch").addEventListener("input", () => { historyOffset = 0; run(loadHistory); });
byId("historyPrev").addEventListener("click", () => { historyOffset = Math.max(0, historyOffset - 50); run(loadHistory); });
byId("historyNext").addEventListener("click", () => { historyOffset += 50; run(loadHistory); });
byId("clearHistory").addEventListener("click", () => run(async () => {
  if (!confirm("确定清空全部历史记录？此操作无法撤销。")) return;
  await api("/api/history/clear", "POST", {}); historyOffset = 0;
  await loadHistory(); status("历史记录已清空");
}));
run(loadSettings);
