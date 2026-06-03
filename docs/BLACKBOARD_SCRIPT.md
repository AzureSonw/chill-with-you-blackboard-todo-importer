# Blackboard Browser Script

`browser/blackboard-to-chill-importer.js` runs inside the Blackboard Calendar page.

It:

1. Scans today through the next 14 days.
2. Opens each reachable calendar day on the page.
3. Reads visible due cards for assignment title, due time, and course line.
4. Falls back to collapsed month-list entries if needed.
5. Formats task titles.
6. Sends JSON to `http://127.0.0.1:29472/blackboard-import`.

The script does not log in, bypass permissions, or send data to external servers.

