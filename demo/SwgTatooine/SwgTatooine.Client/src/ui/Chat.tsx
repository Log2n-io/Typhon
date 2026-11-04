import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
} from 'react';
import { isTyping } from '../camera/camera-input';
import {
  CATEGORY_LABELS,
  CHAT_CATEGORIES,
  MAX_TABS,
  newTabId,
  TAB_NAME_MAX,
  tabLines,
  unreadIn,
  type ChatCategory,
  type ChatTab,
} from '../data/chat-tabs';
import { useChat } from '../state/chat-store';
import { clipUtf8, composeSay } from '../state/chat-text';
import { useUi } from '../state/ui-store';
import { runChatCommand } from '../state/chat-commands';
import { parseChatCommand } from '../data/chat-commands';
import { useWindows } from '../state/windows-store';
import { useBar } from './WindowFrame';

/** The most lines the box grows to before it scrolls. */
const MAX_ROWS = 5;

/**
 * What is being said within earshot (SWG-09), and the box to say something in.
 *
 * The emptiness is informative: a session is only sent speech from within 50 m of its own viewpoint, and only in its own
 * realm, so a silent panel high above the desert is the routing working. Fly down to a town and it fills.
 *
 * <b>Enter opens the box, Enter sends, Shift+Enter is a new line, Escape abandons</b> (Loïc, 2026-10-25). What is sent
 * comes back as the server's own `Chat` — the line here and the bubble over the avatar's head both — so nothing is shown
 * that the server did not accept. A line refused by the one-a-second rate stays in the box.
 *
 * <b>A line starting with `/` is a command, not speech</b> (Loïc, 2026-10-26): `/tip <amount>` gives the selected player
 * that many credits (`GiveCredits`). Its answer is the client's own line — a refusal's reason, or the tip itself once the
 * wallet moves — seen by this player only.
 *
 * <b>Tabs</b> (client.md C44): each a name and the categories it shows, kept with the layout; a tab not shown counts what arrived since it was. ⚙ edits the
 * one shown — its name, its categories — or removes it; + adds one. A /tell is a line of its own category, its sender a button that starts the answer.
 */
export function Chat() {
  const histories = useChat((s) => s.histories);
  const seen = useChat((s) => s.seen);
  const tabs = useWindows((s) => s.layout.chatTabs);
  const [activeId, setActiveId] = useState(tabs[0]?.id ?? 'general');
  const [editing, setEditing] = useState(false);
  const active = tabs.find((t) => t.id === activeId) ?? tabs.at(0);
  const lines = useMemo(() => (active === undefined ? [] : tabLines(histories, active)), [histories, active]);
  const heard = useChat((s) => s.heard);
  const speak = useChat((s) => s.speak);
  const select = useUi((s) => s.select);
  const playing = useUi((s) => s.playing);
  const bottom = useRef<HTMLDivElement>(null);
  const box = useRef<HTMLTextAreaElement>(null);
  const [composing, setComposing] = useState(false);
  const [draft, setDraft] = useState('');
  const [refused, setRefused] = useState(false);
  // Bumped by a compose request, so the box is focused with the caret after the text even when it was open already.
  const [composeSeq, setComposeSeq] = useState(0);
  // A bar (client.md C36): moved, and resized by its corner, while the interface is unlocked.
  const bar = useBar('chat');
  const locked = useWindows((s) => s.layout.locked);
  const storedW = useWindows((s) => s.layout.chatW);
  const storedH = useWindows((s) => s.layout.chatH);
  const [liveSize, setLiveSize] = useState<{ readonly w: number; readonly h: number } | null>(null);
  const sizing = useRef<{ readonly px: number; readonly py: number; readonly w: number; readonly h: number } | null>(
    null,
  );
  // A session that stops playing — logged out, kicked, a god camera — has nobody to speak as: the box closes with it.
  const open = composing && playing;

  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' });
  }, [lines.length, open]);

  // Enter, anywhere but in a field, opens the box. Captured, so a toolbar button left focused by a click is not also
  // pressed; and not while a context menu is open, whose keys are its own.
  useEffect(() => {
    if (!playing || open) {
      return undefined;
    }

    const onKey = (e: KeyboardEvent): void => {
      // Not from inside an element that keeps its own Enter (data-keep-enter): the inventory's count prompt confirms with it, a focused button
      // included, and this listener captures before React's.
      const keeps = e.target instanceof Element && e.target.closest('[data-keep-enter]') !== null;
      if (
        e.key !== 'Enter' ||
        e.repeat ||
        e.isComposing ||
        isTyping(e) ||
        keeps ||
        useUi.getState().contextMenu !== null
      ) {
        return;
      }

      e.preventDefault();
      e.stopPropagation();
      setRefused(false);
      setComposing(true);
    };
    window.addEventListener('keydown', onKey, true);
    return () => {
      window.removeEventListener('keydown', onKey, true);
    };
  }, [playing, open]);

  useEffect(() => {
    const field = box.current;
    if (open && field !== null) {
      field.focus();
      field.setSelectionRange(field.value.length, field.value.length);
    }
  }, [open, composeSeq]);

  // The context menu's "Tip…": the box opens with the command in it, the caret after it.
  useEffect(
    () =>
      useChat.subscribe((state, previous) => {
        const request = state.composeRequest;
        if (request !== null && request !== previous.composeRequest) {
          setRefused(false);
          setDraft(request.text);
          setComposing(true);
          setComposeSeq(request.seq);
        }
      }),
    [],
  );

  const onKeyDown = (e: ReactKeyboardEvent<HTMLTextAreaElement>): void => {
    if (e.key === 'Escape') {
      e.preventDefault();
      setComposing(false);
      setDraft('');
      return;
    }

    // Shift+Enter falls through to the textarea's own new line; an IME's Enter confirms its candidate.
    if (e.key !== 'Enter' || e.shiftKey || e.nativeEvent.isComposing) {
      return;
    }

    e.preventDefault();
    const command = parseChatCommand(draft);
    if (command !== null) {
      if (runChatCommand(command)) {
        setComposing(false);
        setDraft('');
      }

      return;
    }

    const text = composeSay(draft);
    if (text.length === 0) {
      setComposing(false);
      setDraft('');
      return;
    }

    if (!speak(text)) {
      setRefused(true);
      return;
    }

    setComposing(false);
    setDraft('');
  };

  const rows = Math.min(MAX_ROWS, Math.max(1, draft.split('\n').length));

  return (
    <div
      className="panel chat"
      style={chatStyle(bar.style, liveSize ?? (storedW > 0 ? { w: storedW, h: storedH } : null))}
    >
      {bar.grip}
      <div className="chat-tabs" role="tablist" aria-label="Chat tabs">
        {tabs.map((tab) => {
          const unread = tab.id === active?.id ? 0 : unreadIn(histories, tab, seen[tab.id] ?? 0);
          return (
            <button
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={tab.id === active?.id}
              className={tab.id === active?.id ? 'chat-tab active' : unread > 0 ? 'chat-tab unread' : 'chat-tab'}
              title={tab.categories.map((c) => CATEGORY_LABELS[c]).join(', ')}
              onClick={() => {
                // What each has shown is seen: the one left counts from now, the one shown has nothing to count while it is in front.
                if (active !== undefined) {
                  useChat.getState().markSeen(active.id);
                }

                useChat.getState().markSeen(tab.id);
                setActiveId(tab.id);
                setEditing(false);
              }}
            >
              {tab.name}
              {unread > 0 ? <span className="chat-tab-count">{unread > 99 ? '99+' : String(unread)}</span> : null}
            </button>
          );
        })}
        {tabs.length < MAX_TABS ? (
          <button
            type="button"
            className="chat-tab chat-tab-tool"
            title="A new tab"
            onClick={() => {
              const tab: ChatTab = { id: newTabId(tabs), name: 'New tab', categories: CHAT_CATEGORIES };
              useWindows.getState().setChatTabs([...tabs, tab]);
              setActiveId(tab.id);
              setEditing(true);
            }}
          >
            +
          </button>
        ) : null}
        <button
          type="button"
          className={editing ? 'chat-tab chat-tab-tool active' : 'chat-tab chat-tab-tool'}
          title="This tab: its name and what it shows"
          aria-pressed={editing}
          onClick={() => {
            setEditing(!editing);
          }}
        >
          ⚙
        </button>
        <span className="chat-count">{heard} heard</span>
      </div>
      {editing && active !== undefined ? (
        <TabEditor
          tab={active}
          removable={tabs.length > 1}
          onChange={(tab) => {
            useWindows.getState().setChatTabs(tabs.map((t) => (t.id === tab.id ? tab : t)));
          }}
          onRemove={() => {
            const rest = tabs.filter((t) => t.id !== active.id);
            useWindows.getState().setChatTabs(rest);
            setActiveId(rest[0].id);
            setEditing(false);
          }}
        />
      ) : null}
      {lines.length === 0 ? (
        <div className="hint">
          Nothing within earshot. Speech carries 50 m, and only inside the speaker&apos;s own realm — drop the camera
          onto a town to hear it.
        </div>
      ) : (
        <div className="chat-lines">
          {lines.map((line) =>
            line.tell !== undefined ? (
              <div key={line.seq} className="chat-line chat-tell">
                <button
                  className="chat-speaker"
                  title={`Answer ${line.tell.other}`}
                  onClick={() => {
                    useChat.getState().compose(`/tell ${line.tell?.other ?? ''} `);
                  }}
                >
                  {line.tell.outgoing ? `To ${line.tell.other}` : `From ${line.tell.other}`}
                </button>
                <span className="chat-text">{line.text}</span>
              </div>
            ) : line.system === true ? (
              <div key={line.seq} className="chat-line chat-system">
                <span className="chat-speaker">·</span>
                <span className="chat-text">{line.text}</span>
              </div>
            ) : (
              <div key={line.seq} className="chat-line">
                <button
                  className={line.own === true ? 'chat-speaker chat-own' : 'chat-speaker'}
                  title="Select the speaker"
                  onClick={() => {
                    select(line.speaker);
                  }}
                >
                  {line.own === true ? 'You' : `#${String(line.speaker)}`}
                </button>
                <span className="chat-text">{line.text}</span>
              </div>
            ),
          )}
          <div ref={bottom} />
        </div>
      )}
      {open ? (
        <div className="chat-compose">
          <textarea
            ref={box}
            className="chat-input"
            rows={rows}
            value={draft}
            placeholder="Say something…"
            onChange={(e) => {
              setRefused(false);
              setDraft(clipUtf8(e.target.value));
            }}
            onKeyDown={onKeyDown}
            onBlur={() => {
              setComposing(false);
            }}
          />
          <div className="hint">
            {refused
              ? 'Too fast: one line a second. Enter to try again.'
              : 'Enter to say it · /help lists the commands · Esc to cancel'}
          </div>
        </div>
      ) : (
        playing && <div className="hint chat-prompt">Press Enter to talk.</div>
      )}
      {locked ? null : (
        <div
          className="window-grip"
          aria-hidden="true"
          onPointerDown={(e) => {
            const r = (e.currentTarget.parentElement ?? e.currentTarget).getBoundingClientRect();
            sizing.current = { px: e.clientX, py: e.clientY, w: r.width, h: r.height };
            e.currentTarget.setPointerCapture(e.pointerId);
            e.preventDefault();
          }}
          onPointerMove={(e) => {
            const s = sizing.current;
            if (s !== null) {
              setLiveSize({
                w: Math.max(220, Math.min(window.innerWidth - 20, s.w + e.clientX - s.px)),
                // Its top is what moves: the chat is anchored at the bottom, so a drag down shrinks it.
                h: Math.max(120, Math.min(window.innerHeight - 20, s.h - (e.clientY - s.py))),
              });
            }
          }}
          onPointerUp={() => {
            if (sizing.current !== null && liveSize !== null) {
              useWindows.getState().sizeChat(Math.round(liveSize.w), Math.round(liveSize.h));
            }

            sizing.current = null;
            setLiveSize(null);
          }}
        />
      )}
    </div>
  );
}

/** The chat's style: its bar offset, and its size when the player gave it one. */
function chatStyle(offset: CSSProperties, size: { readonly w: number; readonly h: number } | null): CSSProperties {
  return size === null ? offset : { ...offset, width: size.w, height: size.h, maxHeight: 'none' };
}

/** One tab's settings: its name, the categories it shows, and its removal (never the last one). */
function TabEditor({
  tab,
  removable,
  onChange,
  onRemove,
}: {
  readonly tab: ChatTab;
  readonly removable: boolean;
  readonly onChange: (tab: ChatTab) => void;
  readonly onRemove: () => void;
}) {
  const toggle = (category: ChatCategory, on: boolean): void => {
    onChange({
      ...tab,
      categories: on
        ? CHAT_CATEGORIES.filter((c) => c === category || tab.categories.includes(c))
        : tab.categories.filter((c) => c !== category),
    });
  };

  return (
    <div className="chat-tab-editor">
      <label className="chat-tab-name">
        Name
        <input
          type="text"
          value={tab.name}
          maxLength={TAB_NAME_MAX}
          onChange={(e) => {
            const name = e.target.value.slice(0, TAB_NAME_MAX);
            if (name.trim().length > 0) {
              onChange({ ...tab, name });
            }
          }}
        />
      </label>
      <div className="chat-tab-categories">
        {CHAT_CATEGORIES.map((c) => (
          <label key={c}>
            <input
              type="checkbox"
              checked={tab.categories.includes(c)}
              onChange={(e) => {
                toggle(c, e.target.checked);
              }}
            />
            {CATEGORY_LABELS[c]}
          </label>
        ))}
      </div>
      {removable ? (
        <button type="button" className="danger" onClick={onRemove}>
          Remove this tab
        </button>
      ) : null}
    </div>
  );
}
