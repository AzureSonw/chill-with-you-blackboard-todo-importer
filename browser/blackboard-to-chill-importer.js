(async function () {
  const ENDPOINT = "http://127.0.0.1:29472/blackboard-import";
  const DAYS_TO_SCAN = 14;
  const CLICK_DELAY_MS = 700;

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

  function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  function pad(n) {
    return String(n).padStart(2, "0");
  }

  function slug(s) {
    return String(s || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 100) || "item";
  }

  function dateKey(dateParts) {
    return `${dateParts.year}-${pad(dateParts.month + 1)}-${pad(dateParts.day)}`;
  }

  function datePartsFromDate(date) {
    return { year: date.getFullYear(), month: date.getMonth(), day: date.getDate() };
  }

  function dateFromBbAttr(attr) {
    const match = String(attr || "").match(/(20\d{2})-(\d{2})-(\d{2})/);
    if (!match) return null;
    return { year: Number(match[1]), month: Number(match[2]) - 1, day: Number(match[3]) };
  }

  function toDate(dateParts) {
    return new Date(dateParts.year, dateParts.month, dateParts.day);
  }

  function sameDay(a, b) {
    return a && b && a.year === b.year && a.month === b.month && a.day === b.day;
  }

  function parseMonthYear() {
    const candidates = [
      text(document.querySelector(".month h2")),
      text(document.querySelector(".month-title")),
      text(document.querySelector(".month-fixed-title")),
      document.body.innerText
    ].filter(Boolean);

    for (const candidate of candidates) {
      const match = candidate.match(/\b([A-Za-z]+)\s+(20\d{2})\b/);
      if (!match) continue;
      const month = MONTHS[match[1].toLowerCase()];
      if (month !== undefined) return { month, year: Number(match[2]) };
    }

    const now = new Date();
    return { month: now.getMonth(), year: now.getFullYear() };
  }

  function parseSelectedDate() {
    const body = document.body.innerText || "";
    const match = body.match(/\b([A-Za-z]+)\s+(\d{1,2}),\s+(20\d{2})\b/);
    if (match) {
      const month = MONTHS[match[1].toLowerCase()];
      if (month !== undefined) return { year: Number(match[3]), month, day: Number(match[2]) };
    }

    const ym = parseMonthYear();
    const selected =
      document.querySelector(".week-carousel .selected, .week-carousel .active, .fc-state-highlight") ||
      document.querySelector("[aria-selected='true']");
    const dayMatch = text(selected).match(/\b(\d{1,2})\b/);
    return { year: ym.year, month: ym.month, day: dayMatch ? Number(dayMatch[1]) : new Date().getDate() };
  }

  function parseTimeTo24(timeText) {
    const match = String(timeText || "").match(/(\d{1,2})(?::(\d{2}))?\s*(AM|PM)/i);
    if (!match) return { hour: 23, minute: 59 };
    let hour = Number(match[1]);
    const minute = Number(match[2] || "0");
    const ampm = match[3].toUpperCase();
    if (ampm === "PM" && hour !== 12) hour += 12;
    if (ampm === "AM" && hour === 12) hour = 0;
    return { hour, minute };
  }

  function makeIso(dateParts, timeText) {
    const t = parseTimeTo24(timeText);
    return `${dateParts.year}-${pad(dateParts.month + 1)}-${pad(dateParts.day)}T${pad(t.hour)}:${pad(t.minute)}:00`;
  }

  function dateFromTitle(title, fallbackDate) {
    const numeric = String(title || "").match(/\bDue\s+(\d{1,2})\/(\d{1,2})(?:\/(\d{2,4}))?\s+(\d{1,2})(?::(\d{2}))?\s*(AM|PM)?/i);
    if (numeric) {
      let year = fallbackDate.year;
      if (numeric[3]) {
        year = Number(numeric[3]);
        if (year < 100) year += 2000;
      }
      return {
        date: { year, month: Number(numeric[1]) - 1, day: Number(numeric[2]) },
        time: `${numeric[4]}:${numeric[5] || "00"} ${numeric[6] || "PM"}`
      };
    }

    const written = String(title || "").match(/\bDue\s+(?:Sun|Mon|Tue|Wed|Thu|Fri|Sat)(?:day)?[,]?\s+([A-Za-z]+)\s+(\d{1,2})(?:st|nd|rd|th)?\s+by\s+(\d{1,2})(?::(\d{2}))?\s*(AM|PM)?/i);
    if (written) {
      const month = MONTHS[written[1].toLowerCase()];
      if (month !== undefined) {
        return {
          date: { year: fallbackDate.year, month, day: Number(written[2]) },
          time: `${written[3]}:${written[4] || "00"} ${written[5] || "PM"}`
        };
      }
    }

    return null;
  }

  function extractClassCode(course) {
    let value = String(course || "").trim();
    if (!value) return "";

    const afterColon = value.match(/:\s*(.+)$/);
    if (afterColon) value = afterColon[1].trim();

    value = value
      .split(/\s+-\s+/)[0]
      .replace(/\s+/g, " ")
      .trim();

    const codeMatch = value.match(/\b([A-Z]{2,}[A-Z0-9]*-?[A-Z0-9]+)(?:-\d{2,4})?\b/i);
    if (!codeMatch) return "";

    const code = codeMatch[1].toUpperCase();
    return code.length <= 16 ? code : "";
  }

  function cleanAssignmentTitleForDisplay(title) {
    return String(title || "")
      .replace(/\s+-?\s*Due\s+\d{1,2}\/\d{1,2}(?:\/\d{2,4})?\s+\d{1,2}(?::\d{2})?\s*(?:AM|PM)?\s*$/i, "")
      .replace(/\s+-?\s*Due\s+(?:Sun|Mon|Tue|Wed|Thu|Fri|Sat)(?:day)?[,]?\s+[A-Za-z]+\s+\d{1,2}(?:st|nd|rd|th)?\s+by\s+\d{1,2}(?::\d{2})?\s*(?:AM|PM)?\s*$/i, "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function formatTodoTitle(course, assignmentTitle) {
    const assignment = cleanAssignmentTitleForDisplay(assignmentTitle);
    const classCode = extractClassCode(course);
    if (!classCode) return assignment;

    const combined = `${classCode} - ${assignment}`;
    return combined.length <= 64 ? combined : assignment;
  }

  function addTask(tasks, seen, seenSimple, title, dateParts, timeText, course, source, startDate, endDate) {
    title = String(title || "").trim();
    if (!title || /^[-\d]+\s*more$/i.test(title) || /^\d+\s*more$/i.test(title)) return;

    const embedded = dateFromTitle(title, dateParts);
    if (embedded) {
      dateParts = embedded.date;
      timeText = embedded.time;
    }

    const due = makeIso(dateParts, timeText || "11:59 PM");
    const dueDate = new Date(due);
    if (dueDate < startDate || dueDate > endDate) return;

    const simpleKey = slug(title) + "|" + due;
    if (seenSimple.has(simpleKey)) return;
    seenSimple.add(simpleKey);

    const id = slug([course, title, due].filter(Boolean).join("|"));
    if (seen.has(id)) return;
    seen.add(id);

    tasks.push({
      id,
      title: formatTodoTitle(course, title),
      due,
      source
    });
  }

  function collectVisibleDueCards(tasks, seen, seenSimple, dateParts, startDate, endDate) {
    for (const el of Array.from(document.querySelectorAll(".due-item, .fc-event"))) {
      const title = text(el.querySelector(".fc-title")) || text(el).split("Due date:")[0];
      const dueText = text(el.querySelector(".dueDate")) || text(el).match(/Due date:\s*([^∙]+)/)?.[1] || "11:59 PM";
      const courseLine = text(el.querySelector(".content.fc-time"));
      const course = courseLine
        .replace(/Due date:\s*[^∙]+/i, "")
        .replace(/^[\s∙·-]+/, "")
        .trim();
      addTask(tasks, seen, seenSimple, title, dateParts, dueText, course, "visible-calendar-card", startDate, endDate);
    }
  }

  function dayElements() {
    return Array.from(document.querySelectorAll(".month-list > li")).map(li => {
      const attr = li.querySelector("h5[bb-date]")?.getAttribute("bb-date") || "";
      const date = dateFromBbAttr(attr);
      return { li, date };
    }).filter(item => item.date);
  }

  function clickDayFromMonthList(dateParts) {
    const item = dayElements().find(candidate => sameDay(candidate.date, dateParts));
    if (!item) return false;
    const target =
      item.li.querySelector("[role='button']") ||
      item.li.querySelector("bb-svg-icon") ||
      item.li.querySelector(".date") ||
      item.li;

    if (target && typeof target.click === "function") {
      target.click();
      return true;
    }

    if (target && typeof MouseEvent === "function") {
      target.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
      return true;
    }

    return false;
  }

  function collectMonthListFallback(tasks, seen, seenSimple, startDate, endDate) {
    const ym = parseMonthYear();
    for (const dayLi of Array.from(document.querySelectorAll(".month-list > li"))) {
      let dateParts = dateFromBbAttr(dayLi.querySelector("h5[bb-date]")?.getAttribute("bb-date"));
      if (!dateParts) {
        const dayHeader = Array.from(dayLi.childNodes)
          .filter(n => n.nodeType === 3 || (n.nodeType === 1 && !n.classList.contains("event-list")))
          .map(n => text(n))
          .join(" ");
        const dayMatch = (dayHeader || text(dayLi)).match(/\b(?:Sun|Mon|Tue|Wed|Thu|Fri|Sat)\s+(\d{1,2})\b/i);
        if (!dayMatch) continue;
        dateParts = { year: ym.year, month: ym.month, day: Number(dayMatch[1]) };
      }

      for (const item of Array.from(dayLi.querySelectorAll(".event-list li"))) {
        addTask(tasks, seen, seenSimple, text(item), dateParts, "11:59 PM", "", "month-list-fallback", startDate, endDate);
      }
    }
  }

  const tasks = [];
  const seen = new Set();
  const seenSimple = new Set();

  const start = new Date();
  start.setHours(0, 0, 0, 0);
  const end = new Date(start);
  end.setDate(end.getDate() + DAYS_TO_SCAN);
  end.setHours(23, 59, 59, 999);

  for (let i = 0; i <= DAYS_TO_SCAN; i++) {
    const day = new Date(start);
    day.setDate(start.getDate() + i);
    const targetDate = datePartsFromDate(day);
    const clicked = clickDayFromMonthList(targetDate);
    if (clicked) await sleep(CLICK_DELAY_MS);

    const selectedDate = parseSelectedDate();
    collectVisibleDueCards(tasks, seen, seenSimple, sameDay(selectedDate, targetDate) ? selectedDate : targetDate, start, end);
  }

  collectMonthListFallback(tasks, seen, seenSimple, start, end);

  if (!tasks.length) {
    alert("没有在当前 Blackboard Calendar 页面找到未来两周的 due items。请打开 Calendar > Due Dates 页面后再运行。");
    return;
  }

  tasks.sort((a, b) => String(a.due).localeCompare(String(b.due)) || String(a.title).localeCompare(String(b.title)));

  const response = await fetch(ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "text/plain;charset=utf-8" },
    body: JSON.stringify(tasks, null, 2)
  });

  const resultText = await response.text();
  if (!response.ok) {
    alert(`发送到 Chill With You 插件失败：HTTP ${response.status}\n${resultText}`);
    return;
  }

  alert(`已自动扫描未来 ${DAYS_TO_SCAN} 天，并发送 ${tasks.length} 个 Blackboard due item 到 Chill With You。`);
})();
