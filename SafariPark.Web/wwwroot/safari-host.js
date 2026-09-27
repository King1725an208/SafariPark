// DOM helpers the managed side calls through [JSImport]. Kept out of main.js because main.js
// is the dotnet bootstrap and importing it a second time would re-run it.

export function setStatus(text) {
    document.getElementById('hud-status').textContent = text;
}

export function setStats(text) {
    document.getElementById('hud-stats').textContent = text;
}
