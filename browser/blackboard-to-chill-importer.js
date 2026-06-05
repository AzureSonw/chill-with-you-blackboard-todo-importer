(function () {
  const ENDPOINT = "http://127.0.0.1:29472/blackboard-import";
  const DAYS_TO_SCAN = 21;
  const MAX_WARN_COUNT = 60;
  const SUBJECT_CODES = [
    // Common general university subject codes.
    "AAMW", "ACCT", "AFRC", "AFST", "ANTH", "ARAB", "ARCH", "ARTH", "ASAM", "ASRM",
    "BE", "BIO", "BIOL", "BLAW", "BME", "BUS", "BUSN",
    "CHEM", "CHIN", "CINE", "CIS", "CI", "CIVC", "CIVE", "CLST", "COM", "COMM", "CRIM", "CS",
    "DANC", "DIGM", "DSCI",
    "EAS", "ECE", "ECON", "EDUC", "ENGL", "ENGR", "ENTP", "ENVS", "ESL", "EVAL",
    "FILM", "FIN", "FMST", "FREN",
    "GAME", "GSWS",
    "HIST", "HSCI", "HSOC", "HUM",
    "INFO", "INTB", "ISYS", "ITAL",
    "JAPN", "JWST",
    "KOR",
    "LAW", "LING", "LIT",
    "MATH", "MEM", "MGMT", "MKTG", "MUS", "MUSC",
    "NURS", "NUTR",
    "OPIM", "ORGB",
    "PHIL", "PHYS", "PPE", "PSCI", "PSYC",
    "REAL", "RELS", "ROBO",
    "SOC", "SPAN", "STAT", "STSC", "SYS",
    "THTR", "TVST", "UNIV", "URBS", "VSCM", "WRIT"
  ];

  const MONTHS = {
    jan: 0, january: 0,
    feb: 1, february: 1,
    mar: 2, march: 2,
    apr: 3, april: 3,
    may: 4,
    jun: 5, june: 5,
    jul: 6, july: 6,
    aug: 7, august: 7,
    sep: 8, sept: 8, september: 8,
    oct: 9, october: 9,
    nov: 10, november: 10,
    dec: 11, december: 11
  };

  function text(el) {
    return (el && (el.innerText || el.textContent) || "").replace(/\s+/g, " ").trim();
  }

  function pad(n) {
    return String(n).padStart(2, "0");
  }

  function slug(s) {
    return String(s || "")
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-|-$/g, "")
      .slice(0, 90) || "blackboard-item";
  }

  function normalizeTitle(s) {
    return cleanTitle(s).toLowerCase();
  }

  function cleanTitle(s) {
    return String(s || "")
      .replace(/^(today|tomorrow|yesterday|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\s*-\s*[a-z]+\s+\d{1,2},\s*20\d{2}\s+/i, "")
      .replace(/\s+-?\s*due(?: date)?:?.*$/i, "")
      .replace(/\s+due\s+\d{1,2}\/\d{1,2}.*$/i, "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function stripSubjectPrefix(s) {
    let value = String(s || "").replace(/\s+/g, " ").trim();
    for (const code of SUBJECT_CODES) {
      value = value.replace(new RegExp(`^${escapeRegex(code)}\\s*-\\s*`, "i"), "");
    }
    return value.trim();
  }

  function taskKey(title, due) {
    return `${normalizeTitle(stripSubjectPrefix(title))}|${String(due || "").slice(0, 10)}`;
  }

  function escapeRegex(s) {
    return String(s || "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  function extractSubject(s) {
    const value = String(s || "").replace(/\s+/g, " ").trim();
    if (!value) return "";

    const courseNumber = value.match(/\b([A-Z]{2,6})\s*[-_ ]?\d{2,4}[A-Z]?\b/);
    if (courseNumber) return courseNumber[1].toUpperCase();

    for (const code of SUBJECT_CODES) {
      if (new RegExp(`\\b${escapeRegex(code)}\\b`, "i").test(value)) {
        return code;
      }
    }

    return "";
  }

  function countDueDates(s) {
    return (String(s || "").match(/due date:/gi) || []).length;
  }

  function readAttrs(el) {
    if (!el || !el.getAttribute) return [];
    return [
      "aria-label",
      "title",
      "href",
      "data-course-id",
      "data-course-name",
      "data-course-title",
      "data-course-code"
    ].map(name => el.getAttribute(name)).filter(Boolean);
  }

  function toIso(parts, time) {
    const t = time || { hour: 23, minute: 59 };
    return `${parts.year}-${pad(parts.month + 1)}-${pad(parts.day)}T${pad(t.hour)}:${pad(t.minute)}:00`;
  }

  function parseTime(s) {
    const m = String(s || "").match(/\b(\d{1,2})(?::(\d{2}))?\s*(AM|PM)\b/i);
    if (!m) return { hour: 23, minute: 59 };

    let hour = Number(m[1]);
    const minute = Number(m[2] || "0");
    const ampm = m[3].toUpperCase();
    if (ampm === "PM" && hour !== 12) hour += 12;
    if (ampm === "AM" && hour === 12) hour = 0;
    return { hour, minute };
  }

  function parseMonthNameDate(s) {
    const m = String(s || "").match(/\b([A-Za-z]+)\s+(\d{1,2})(?:st|nd|rd|th)?(?:,\s*)?(20\d{2})?\b/);
    if (!m) return null;

    const month = MONTHS[m[1].toLowerCase()];
    if (month === undefined) return null;

    const now = new Date();
    return {
      year: m[3] ? Number(m[3]) : now.getFullYear(),
      month,
      day: Number(m[2])
    };
  }

  function parseNumericDate(s) {
    const m = String(s || "").match(/\b(\d{1,2})\/(\d{1,2})(?:\/(\d{2,4}))?\b/);
    if (!m) return null;

    let year = m[3] ? Number(m[3]) : new Date().getFullYear();
    if (year < 100) year += 2000;
    return { year, month: Number(m[1]) - 1, day: Number(m[2]) };
  }

  function parseBbDateAttr(el) {
    const holder = el && el.closest && (el.closest("li") || el.closest("[bb-date]"));
    const attrNode = holder && holder.querySelector("h5[bb-date]");
    const attr = attrNode && attrNode.getAttribute("bb-date");
    const m = String(attr || "").match(/(20\d{2})-(\d{2})-(\d{2})/);
    if (!m) return null;
    return { year: Number(m[1]), month: Number(m[2]) - 1, day: Number(m[3]) };
  }

  function parseDue(raw, el) {
    const value = String(raw || "");
    const dueSlice = (value.match(/due(?: date)?:?\s*([^|]+)$/i) || [null, value])[1];
    const date = parseNumericDate(dueSlice) || parseMonthNameDate(dueSlice) || parseBbDateAttr(el);
    if (!date) return null;
    return toIso(date, parseTime(dueSlice));
  }

  function findTitle(el, raw) {
    const value = String(raw || text(el));
    const beforeDueDate = value.split(/due date:/i)[0];
    const candidateFromRaw = cleanTitle(beforeDueDate);
    if (candidateFromRaw && !extractSubject(candidateFromRaw)) {
      return candidateFromRaw;
    }

    const titleNode = el.querySelector && el.querySelector(".fc-title,h3,h4,[class*='title' i]");
    const candidate = text(titleNode) || beforeDueDate;
    return cleanTitle(candidate);
  }

  function findItemRoot(el) {
    let root = el;
    for (let parent = el; parent && parent !== document.body; parent = parent.parentElement) {
      const raw = text(parent);
      if (!/due date:/i.test(raw)) continue;
      if (countDueDates(raw) > 1) break;
      root = parent;
    }
    return root;
  }

  function findCourse(el, raw) {
    const candidates = [];
    const add = value => {
      const cleaned = cleanTitle(String(value || "").replace(/due(?: date)?:.*$/i, ""));
      if (cleaned && candidates.indexOf(cleaned) < 0) candidates.push(cleaned);
    };

    const selectors = [
      ".content.fc-time",
      "[class*='course' i]",
      "[class*='class' i]",
      "[class*='term' i]",
      "[data-course-id]",
      "[data-course-name]",
      "[data-course-title]",
      "[data-course-code]",
      "a",
      "a[href*='course' i]",
      "a[href*='course_id' i]"
    ].join(",");

    const roots = [el];
    const holder = el.closest && el.closest("li,article,[role='listitem'],.due-item,.fc-event");
    if (holder && holder !== el) roots.push(holder);

    for (const root of roots) {
      if (!root) continue;
      add(text(root.querySelector && root.querySelector(selectors)));
      for (const attr of readAttrs(root)) add(attr);
      for (const node of Array.from(root.querySelectorAll ? root.querySelectorAll(selectors) : [])) {
        add(text(node));
        for (const attr of readAttrs(node)) add(attr);
      }
    }

    for (let parent = el.parentElement; parent && parent !== document.body && candidates.length < 40; parent = parent.parentElement) {
      for (const attr of readAttrs(parent)) add(attr);
      add(text(parent.querySelector && parent.querySelector(selectors)));
    }

    add(raw);
    add(document.title);
    for (const node of Array.from(document.querySelectorAll("nav,[aria-label*='breadcrumb' i],.breadcrumb,h1,h2"))) {
      add(text(node));
      for (const attr of readAttrs(node)) add(attr);
    }

    for (const candidate of candidates) {
      const subject = extractSubject(candidate);
      if (subject) return subject;
    }

    return "";
  }

  function addCandidate(map, el) {
    const root = findItemRoot(el);
    const raw = text(root);
    if (!raw || !/due|quiz|assignment|homework|discussion|project|reflection|survey|review|deliverable/i.test(raw)) {
      return;
    }

    const title = findTitle(root, raw);
    const due = parseDue(raw, root);
    if (!title || !due) return;

    const dueDate = new Date(due);
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    const end = new Date(start);
    end.setDate(end.getDate() + DAYS_TO_SCAN);
    end.setHours(23, 59, 59, 999);
    if (dueDate < start || dueDate > end) return;

    const subject = findCourse(root, raw);
    const key = taskKey(title, due);
    const displayTitle = subject && !new RegExp(`^${escapeRegex(subject)}\\b`, "i").test(title)
      ? `${subject} - ${title}`
      : title;

    const task = {
      id: slug(key),
      title: displayTitle,
      due,
      source: "blackboard-bookmarklet",
      subject
    };

    const old = map.get(key);
    if (!old || (!old.subject && task.subject) || String(task.due).localeCompare(String(old.due)) < 0) {
      map.set(key, task);
    }
  }

  function inferSeriesSubjects(tasks) {
    const seriesSubjects = new Map();
    for (const task of tasks) {
      const baseTitle = stripSubjectPrefix(task.title);
      const m = baseTitle.match(/^(assignment|homework|quiz|project)\s+\d+\b/i);
      if (m && task.subject) {
        seriesSubjects.set(m[1].toLowerCase(), task.subject);
      }
    }

    return tasks.map(task => {
      if (task.subject) return task;
      const baseTitle = stripSubjectPrefix(task.title);
      const m = baseTitle.match(/^(assignment|homework|quiz|project)\s+\d+\b/i);
      const subject = m && seriesSubjects.get(m[1].toLowerCase());
      if (!subject) return task;

      const title = `${subject} - ${baseTitle}`;
      return Object.assign({}, task, {
        id: slug(`${subject} ${baseTitle}`),
        title,
        subject
      });
    });
  }

  function dedupeTasks(tasks) {
    const map = new Map();
    for (const task of tasks) {
      const key = taskKey(task.title, task.due);
      const old = map.get(key);
      if (!old || (!old.subject && task.subject)) {
        map.set(key, task);
      }
    }

    return Array.from(map.values());
  }

  function collect() {
    const selector = [
      ".due-item",
      ".fc-event",
      ".event-list li",
      "li",
      "article",
      "[role='listitem']"
    ].join(",");

    const map = new Map();
    const candidates = new Set(Array.from(document.querySelectorAll(selector)));
    for (const el of Array.from(document.querySelectorAll("body *"))) {
      if (/due date:/i.test(text(el))) {
        candidates.add(findItemRoot(el));
      }
    }

    for (const el of Array.from(candidates)) {
      const nested = el.querySelector && el.querySelector(".due-item,.fc-event,.event-list li,article,[role='listitem']");
      if (nested && nested !== el && !el.matches(".due-item,.fc-event,.event-list li")) continue;
      addCandidate(map, el);
    }

    return dedupeTasks(inferSeriesSubjects(Array.from(map.values())))
      .sort((a, b) => String(a.due).localeCompare(String(b.due)) || String(a.title).localeCompare(String(b.title)));
  }

  async function sendToGame(tasks) {
    const json = JSON.stringify(tasks, null, 2);
    const response = await fetch(ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "text/plain;charset=utf-8" },
      body: json
    });

    const resultText = await response.text();
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}: ${resultText}`);
    }

    return resultText;
  }

  function downloadJsonFallback(tasks) {
    const json = JSON.stringify(tasks, null, 2);
    const blob = new Blob([json], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "blackboard_tasks.json";
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(json).catch(() => {});
    }
  }

  async function main() {
    const tasks = collect();
    if (!tasks.length) {
      alert("没有找到 Blackboard due items。请打开 Blackboard Calendar / Due Dates 页面后再点书签。");
      return;
    }

    const lines = tasks.slice(0, 12).map(t => `${t.due.slice(5, 10)}  ${t.title}`);
    const more = tasks.length > 12 ? `\n...还有 ${tasks.length - 12} 条` : "";
    const warning = tasks.length > MAX_WARN_COUNT ? "\n\n注意：条数偏多，请确认不是整月重复列表。" : "";
    const ok = confirm(`将发送 ${tasks.length} 条 Blackboard due item 到 Chill With You：\n\n${lines.join("\n")}${more}${warning}\n\n确定发送吗？`);
    if (!ok) return;

    try {
      const result = await sendToGame(tasks);
      alert(`已发送到 Chill With You，共 ${tasks.length} 条。\n${result}`);
    } catch (err) {
      const fallback = confirm(`发送失败：${err.message}\n\n请确认游戏已启动且插件已加载。\n要改为下载 blackboard_tasks.json 吗？`);
      if (fallback) {
        downloadJsonFallback(tasks);
      }
    }
  }

  main().catch(err => {
    alert("Blackboard 导入脚本出错：" + err.message);
  });
})();
