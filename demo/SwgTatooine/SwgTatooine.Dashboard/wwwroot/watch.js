// "Watch this bot" (bot-telemetry.md § 7): opens the 3D client's god camera on a traced bot, then steers that same window to the next bot asked
// for. The client reads the first from its URL and the rest from these messages (SwgTatooine.Client src/app/watch-link.ts); the game server takes
// no part.
//
// The button calls this straight from its own onclick, not through Blazor: a popup must be opened in the click's own user activation, and a round
// trip to the server and back is not guaranteed to land inside it. One window, named, so a second Watch steers the client already open instead
// of loading another.
(() => {
    const WINDOW_NAME = 'tatooine-watch';
    let win = null;
    let watched = null;   // { bot, netId, realm, origin } of the last bot sent

    /** The client's base URL: --client-url when the dashboard was given one, else this page's host at the game server's port. */
    function clientBase(clientUrl, serverPort) {
        return new URL(clientUrl || `${location.protocol}//${location.hostname}:${serverPort}/`, location.href);
    }

    function send(bot, origin) {
        win.postMessage({ kind: 'swg-follow', netId: bot.netId, realm: bot.realm, x: bot.x, z: bot.z, bot: bot.bot }, origin);
        watched = { bot: bot.bot, netId: bot.netId, realm: bot.realm, origin };
    }

    /** The Watch button: opens the client on the button's bot the first time (or after it was closed), posts to it after. */
    function watch(button) {
        const d = button.dataset;
        const bot = { netId: Number(d.netId), realm: Number(d.realm), x: Number(d.x), z: Number(d.z), bot: Number(d.bot) };
        const base = clientBase(d.clientUrl, d.serverPort);
        if (win !== null && !win.closed) {
            send(bot, base.origin);
            win.focus();
            return;
        }

        const url = new URL(base);
        url.searchParams.set('as', 'god');
        url.searchParams.set('follow', String(bot.netId));
        url.searchParams.set('realm', String(bot.realm));
        url.searchParams.set('x', String(bot.x));
        url.searchParams.set('z', String(bot.z));
        url.searchParams.set('bot', String(bot.bot));
        win = window.open(url.toString(), WINDOW_NAME);
        watched = win === null ? null : { bot: bot.bot, netId: bot.netId, realm: bot.realm, origin: base.origin };
    }

    /**
     * Each report, from the page: when the watched bot's body changed realm or netId — a door, a dungeon, a shuttle — the client is told again, so
     * it follows the bot across. Never opens a window: only a click may.
     */
    function update(bots) {
        if (watched === null || win === null || win.closed) {
            return;
        }

        const now = bots.find((b) => b.bot === watched.bot);
        if (now && now.netId !== 0 && (now.netId !== watched.netId || now.realm !== watched.realm)) {
            send(now, watched.origin);
        }
    }

    window.tatooineWatch = { watch, update };
})();
