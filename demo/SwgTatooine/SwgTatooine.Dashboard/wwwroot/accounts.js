// The accounts page's open sections (Level and credits, Inventory, Add an item), kept in this browser's localStorage as a comma-separated list so they
// stay as the operator left them across reloads and sessions. Storage can be blocked (a private window, cleared site data): then nothing is kept and
// every section starts closed.
(() => {
    const KEY = 'swg.accounts.open';
    window.swgAccounts = {
        load() {
            try {
                return localStorage.getItem(KEY) || '';
            } catch {
                return '';
            }
        },
        save(value) {
            try {
                localStorage.setItem(KEY, value);
            } catch {
                // Not kept; the page works the same.
            }
        },
    };
})();
