import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { isTyping } from '../camera/camera-input';
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
 */
export function Chat() {
  const lines = useChat((s) => s.lines);
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
      if (e.key !== 'Enter' || e.repeat || e.isComposing || isTyping(e) || useUi.getState().contextMenu !== null) {
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
      <h3>
        Chat <span className="chat-count">{heard} heard</span>
      </h3>
      {lines.length === 0 ? (
        <div className="hint">
          Nothing within earshot. Speech carries 50 m, and only inside the speaker&apos;s own realm — drop the camera
          onto a town to hear it.
        </div>
      ) : (
        <div className="chat-lines">
          {lines.map((line, i) =>
            line.system === true ? (
              <div key={`system-${String(line.atMs)}-${String(i)}`} className="chat-line chat-system">
                <span className="chat-speaker">·</span>
                <span className="chat-text">{line.text}</span>
              </div>
            ) : (
              <div key={`${line.tick}-${line.speaker}-${i}`} className="chat-line">
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
