import { useEffect, useRef } from 'react';
import { useChat } from '../state/chat-store';
import { useUi } from '../state/ui-store';

/**
 * What is being said within earshot (SWG-09).
 *
 * The emptiness is informative: a session is only sent speech from within 50 m of its own viewpoint, and only in its own
 * realm, so a silent panel high above the desert is the routing working. Fly down to a town and it fills.
 */
export function Chat() {
  const lines = useChat((s) => s.lines);
  const heard = useChat((s) => s.heard);
  const select = useUi((s) => s.select);
  const bottom = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' });
  }, [lines.length]);

  return (
    <div className="panel chat">
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
          {lines.map((line, i) => (
            <div key={`${line.tick}-${line.speaker}-${i}`} className="chat-line">
              <button
                className="chat-speaker"
                title="Select the speaker"
                onClick={() => {
                  select(line.speaker);
                }}
              >
                #{line.speaker}
              </button>
              <span className="chat-text">{line.text}</span>
            </div>
          ))}
          <div ref={bottom} />
        </div>
      )}
    </div>
  );
}
