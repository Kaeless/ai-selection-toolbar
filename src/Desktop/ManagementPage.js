"use strict";
// The launch fragment is never sent over HTTP. Keep the token only in this tab.
const token = location.hash.slice(1);
history.replaceState(null, "", location.pathname);
const byId = id => document.getElementById(id);
const status = message => { byId("status").textContent = message; };
let customActions = [];
let apiProfiles = [];
let activeApiId = "";
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
  document.documentElement.style.setProperty("--toolbar-accent", color);
  const answerColor = byId("answerColor").value.toUpperCase();
  byId("answerColorValue").textContent = answerColor;
  for (const id of ["toolbarBackgroundColor", "toolbarBorderColor", "answerBorderColor"])
    byId(id + "Value").textContent = byId(id).value.toUpperCase();
  const preview = byId("toolbarPreview");
  preview.classList.toggle("compact", byId("toolbarStyle").value === "compact");
  while (preview.children.length > 5) preview.lastElementChild.remove();
  for (const item of customActions) {
    const chip = document.createElement("span"); chip.textContent = item.Name || "新按钮"; preview.append(chip);
  }
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, character => ({"&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;"}[character]));
}
function renderIndexMarkdown(value) {
  return escapeHtml(String(value || "").replace(/\s+/g, " ").trim() || "未命名记录")
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/\*([^*]+)\*/g, "<em>$1</em>");
}
function renderInlineMarkdown(value) {
  const code = [];
  let html = escapeHtml(value).replace(/`([^`]+)`/g, (_, content) => {
    code.push("<code>" + content + "</code>");
    return "\u0000" + (code.length - 1) + "\u0000";
  });
  html = html
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/__([^_]+)__/g, "<strong>$1</strong>")
    .replace(/\*([^*]+)\*/g, "<em>$1</em>")
    .replace(/_([^_]+)_/g, "<em>$1</em>");
  return html.replace(/\u0000(\d+)\u0000/g, (_, index) => code[Number(index)]);
}
function renderMarkdown(value) {
  const lines = String(value || "").replace(/\r\n?/g, "\n").split("\n");
  const output = [];
  let code = null;
  let listType = null;
  const closeList = () => {
    if (listType) output.push("</" + listType + ">");
    listType = null;
  };
  for (const line of lines) {
    const fence = line.match(/^\s*```(?:[^\s]*)\s*$/);
    if (fence) {
      if (code === null) { closeList(); code = []; }
      else { output.push("<pre><code>" + escapeHtml(code.join("\n")) + "</code></pre>"); code = null; }
      continue;
    }
    if (code !== null) { code.push(line); continue; }
    if (!line.trim()) { closeList(); continue; }
    const heading = line.match(/^\s*(#{1,6})\s+(.+?)\s*#*\s*$/);
    if (heading) { closeList(); const level = heading[1].length; output.push(`<h${level}>${renderInlineMarkdown(heading[2])}</h${level}>`); continue; }
    const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
    const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
    if (unordered || ordered) {
      const nextType = ordered ? "ol" : "ul";
      if (listType !== nextType) { closeList(); output.push("<" + nextType + ">"); listType = nextType; }
      output.push("<li>" + renderInlineMarkdown((ordered || unordered)[1]) + "</li>");
      continue;
    }
    closeList();
    output.push("<p>" + renderInlineMarkdown(line) + "</p>");
  }
  if (code !== null) output.push("<pre><code>" + escapeHtml(code.join("\n")) + "</code></pre>");
  closeList();
  return output.join("") || "<p class=\"empty\">无回答内容</p>";
}
function parseHistoryDate(value) {
  if (typeof value === "number") return new Date(value);
  const text = String(value || "");
  const dotNet = text.match(/\/Date\((\d+)\)\//);
  if (dotNet) return new Date(Number(dotNet[1]));
  return new Date(text);
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

function editApi(profile) {
  byId("apiEditorTitle").textContent = profile ? "编辑 API" : "新增 API";
  byId("apiId").value = profile?.Id || "";
  byId("apiName").value = profile?.Name || "";
  byId("apiBaseUrl").value = profile?.ApiBaseUrl || "";
  byId("model").value = profile?.Model || "";
  byId("apiKey").value = "";
  byId("apiKey").placeholder = profile?.HasApiKey ? "已保存密钥，留空保持不变" : "可留空用于本地接口";
  byId("makeActive").checked = !profile || profile.Id === activeApiId;
  byId("apiName").focus();
}

function renderApiProfiles() {
  const list = byId("apiProfileList"); list.replaceChildren();
  if (!apiProfiles.length) {
    const empty = document.createElement("div"); empty.className = "card empty";
    empty.textContent = "尚未配置 API。添加后即可在这里切换当前连接。"; list.append(empty); return;
  }
  for (const profile of apiProfiles) {
    const card = document.createElement("article"); card.className = "api-profile";
    if (profile.Id === activeApiId) card.classList.add("active");
    const main = document.createElement("div"); main.className = "api-profile-main";
    const title = document.createElement("strong"); title.textContent = profile.Name || "未命名 API";
    const detail = document.createElement("small");
    detail.textContent = (profile.Model || "未填写模型") + " · " + (profile.ApiBaseUrl || "未填写地址");
    main.append(title, detail);
    const actions = document.createElement("div"); actions.className = "api-profile-actions";
    if (profile.Id === activeApiId) {
      const badge = document.createElement("span"); badge.className = "active-badge"; badge.textContent = "当前使用"; actions.append(badge);
    } else {
      const select = document.createElement("button"); select.type = "button"; select.className = "secondary"; select.textContent = "设为当前";
      select.addEventListener("click", () => run(async () => {
        await api("/api/connection/select", "POST", {id: profile.Id});
        await loadSettings(); status("已切换到 " + profile.Name);
      })); actions.append(select);
    }
    const edit = document.createElement("button"); edit.type = "button"; edit.className = "secondary"; edit.textContent = "编辑";
    edit.addEventListener("click", () => editApi(profile)); actions.append(edit);
    const remove = document.createElement("button"); remove.type = "button"; remove.className = "danger-quiet"; remove.textContent = "删除";
    remove.disabled = apiProfiles.length <= 1;
    remove.addEventListener("click", () => run(async () => {
      if (!confirm("删除 API 配置“" + profile.Name + "”？")) return;
      await api("/api/connection/delete", "POST", {id: profile.Id}); await loadSettings(); status("API 配置已删除");
    })); actions.append(remove);
    card.append(main, actions); list.append(card);
  }
}

async function loadSettings() {
  const value = await api("/api/settings");
  byId("appVersion").textContent = value.Version ? "v" + value.Version : "v—";
  byId("appAuthor").textContent = value.Author ? "作者：" + value.Author : "作者：—";
  byId("autoShow").checked = !!value.AutoShow;
  byId("startOnLogin").checked = !!value.StartOnLogin;
  byId("language").value = value.TargetLanguage || "";
  byId("notesDirectory").value = value.NotesDirectory || "";
  apiProfiles = value.ApiProfiles || [];
  activeApiId = value.ActiveApiId || "";
  renderApiProfiles();
  const editingId = byId("apiId").value;
  editApi(apiProfiles.find(x => x.Id === editingId) || apiProfiles.find(x => x.Id === activeApiId) || null);
  byId("toolbarStyle").value = value.ToolbarStyle === "compact" ? "compact" : "standard";
  byId("toolbarColor").value = /^#[0-9a-f]{6}$/i.test(value.ToolbarAccentColor || "") ? value.ToolbarAccentColor : "#4F46E5";
  byId("toolbarBackgroundColor").value = /^#[0-9a-f]{6}$/i.test(value.ToolbarBackgroundColor || "") ? value.ToolbarBackgroundColor : "#18202E";
  byId("toolbarBorderColor").value = /^#[0-9a-f]{6}$/i.test(value.ToolbarBorderColor || "") ? value.ToolbarBorderColor : "#0B1020";
  byId("answerColor").value = /^#[0-9a-f]{6}$/i.test(value.AnswerBackgroundColor || "") ? value.AnswerBackgroundColor : "#F8FAFC";
  byId("answerBorderColor").value = /^#[0-9a-f]{6}$/i.test(value.AnswerBorderColor || "") ? value.AnswerBorderColor : "#0B1020";
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
    ToolbarBackgroundColor: byId("toolbarBackgroundColor").value.toUpperCase(),
    ToolbarBorderColor: byId("toolbarBorderColor").value.toUpperCase(),
    AnswerBackgroundColor: byId("answerColor").value.toUpperCase(),
    AnswerBorderColor: byId("answerBorderColor").value.toUpperCase(),
    CustomActions: customActions.map(x => ({Id: x.Id, Name: x.Name.trim(), Prompt: x.Prompt.trim()}))
  });
  await loadSettings(); // Retrieve generated IDs for new buttons.
  status("设置已保存，工具栏会在下次显示时更新");
}

async function loadHistory() {
  const request = ++historyRequest;
  const query = byId("historySearch").value.trim();
  const items = await api("/api/history?search=" + encodeURIComponent(query) + "&limit=200");
  if (request !== historyRequest) return;
  const list = byId("historyList"); list.replaceChildren();
  const index = byId("historyIndex"); index.replaceChildren();
  byId("historyIndexCount").textContent = items.length ? "· " + items.length + " 条" : "";
  if (!items.length) {
    const empty = document.createElement("div"); empty.className = "card empty"; empty.textContent = "没有匹配的历史记录"; list.append(empty);
    index.textContent = "本页没有记录"; return;
  }
  let previousDate = "";
  items.forEach((item, position) => {
    const card = document.createElement("article"); card.className = "entry";
    card.id = "history-entry-" + request + "-" + position;
    const meta = document.createElement("div"); meta.className = "meta";
    const date = parseHistoryDate(item.TimeUtc);
    const dateLabel = isNaN(date.getTime()) ? "日期未知" : date.toLocaleDateString();
    if (dateLabel !== previousDate) {
      const group = document.createElement("div"); group.className = "history-index-date"; group.textContent = dateLabel;
      index.append(group); previousDate = dateLabel;
    }
    const configuredAction = (item.Action || "").startsWith("custom:")
      ? customActions.find(x => x.Id === item.Action.slice(7)) : null;
    const actionLabels = {explain: "了解", explain_detailed: "详细解释", translate: "翻译", ask: "提问"};
    const actionName = (item.Action || "").startsWith("custom:")
      ? "自定义 · " + (configuredAction?.Name || "操作") : (actionLabels[item.Action] || item.Action || "操作");
    meta.textContent = (isNaN(date.getTime()) ? dateLabel : date.toLocaleString()) + "  ·  " + actionName + "  ·  " + (item.Application || "未知程序");
    const indexItem = document.createElement("div"); indexItem.className = "history-index-item";
    const jump = document.createElement("button"); jump.type = "button";
    const title = document.createElement("strong"); title.innerHTML = renderIndexMarkdown(item.Selection);
    const subtitle = document.createElement("small"); subtitle.textContent = actionName;
    jump.append(title, subtitle);
    jump.addEventListener("click", () => {
      for (const button of index.querySelectorAll("button")) button.classList.remove("active");
      jump.classList.add("active"); card.scrollIntoView({behavior: "smooth", block: "start"});
    });
    const deleteIndex = document.createElement("button"); deleteIndex.type = "button"; deleteIndex.className = "history-index-delete";
    deleteIndex.textContent = "🗑"; deleteIndex.title = "删除当前记录"; deleteIndex.setAttribute("aria-label", "删除当前记录");
    deleteIndex.addEventListener("click", () => run(async () => {
      if (!confirm("删除这条历史记录？")) return;
      await api("/api/history/delete", "POST", {id: item.Id}); await loadHistory(); status("已删除历史记录");
    }));
    indexItem.append(jump, deleteIndex); index.append(indexItem);
    const selection = document.createElement("div"); selection.className = "selection"; selection.textContent = item.Selection || "";
    card.append(meta, selection);
    if (item.Prompt) { const question = document.createElement("div"); question.className = "question"; question.textContent = "提问：" + item.Prompt; card.append(question); }
    const result = document.createElement("div"); result.className = "result"; result.innerHTML = renderMarkdown(item.Result || item.Response || "");
    const footer = document.createElement("div"); footer.className = "entry-footer";
    const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "删除记录";
    remove.addEventListener("click", () => run(async () => {
      if (!confirm("删除这条历史记录？")) return;
      await api("/api/history/delete", "POST", {id: item.Id}); await loadHistory(); status("已删除历史记录");
    }));
    footer.append(remove); card.append(result, footer); list.append(card);
  });
}

for (const [name, [tab]] of Object.entries(sections)) byId(tab).addEventListener("click", () => showSection(name));
byId("saveSettings").addEventListener("click", () => run(saveSettings));
byId("saveToolbar").addEventListener("click", () => run(saveSettings));
byId("toolbarStyle").addEventListener("change", updatePreview);
byId("toolbarColor").addEventListener("input", updatePreview);
byId("toolbarBackgroundColor").addEventListener("input", updatePreview);
byId("toolbarBorderColor").addEventListener("input", updatePreview);
byId("answerColor").addEventListener("input", updatePreview);
byId("answerBorderColor").addEventListener("input", updatePreview);
byId("addCustomAction").addEventListener("click", () => {
  if (customActions.length >= 8) return;
  customActions.push({Id: "", Name: "", Prompt: ""}); renderCustomActions();
});
byId("newConnection").addEventListener("click", () => editApi(null));
byId("saveConnection").addEventListener("click", () => run(async () => {
  const name = byId("apiName").value.trim();
  if (!name) throw new Error("请填写配置名称");
  await api("/api/connection", "POST", {Id: byId("apiId").value, Name: name,
    ApiBaseUrl: byId("apiBaseUrl").value.trim(), Model: byId("model").value.trim(),
    ApiKey: byId("apiKey").value, MakeActive: byId("makeActive").checked});
  await loadSettings(); status("API 配置已保存");
}));
byId("chooseExe").addEventListener("click", () => byId("exeFile").click());
byId("exeFile").addEventListener("change", event => run(async () => {
  const file = event.target.files?.[0];
  if (!file) return;
  try {
    await api("/api/exclusions/add", "POST", {application: file.name});
    await loadExclusions(); status("已排除 " + file.name);
  } finally { event.target.value = ""; }
}));
byId("historySearch").addEventListener("input", () => run(loadHistory));
byId("clearHistory").addEventListener("click", () => run(async () => {
  if (!confirm("确定清空全部历史记录？此操作无法撤销。")) return;
  await api("/api/history/clear", "POST", {});
  await loadHistory(); status("历史记录已清空");
}));
run(loadSettings);
