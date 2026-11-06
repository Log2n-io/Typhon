import { ageText, MAIL_SUBJECT_MAX, MAIL_TEXT_MAX, senderOf, utf8Length, type MailFull } from '../data/mail';
import { useMail, type MailDraft } from '../state/mail-store';
import { WindowFrame } from './WindowFrame';

/**
 * The mail (mail.md § 6): the inbox newest first — unread in bold, who from, how long ago — and beside it the mail open or the one being written.
 * <kbd>N</kbd> and the toolbar open it; a new mail says so in the chat. Player mail is answered with Reply; the game's says what it is about in its text.
 */
export function MailWindow() {
  const open = useMail((s) => s.open);
  const inbox = useMail((s) => s.inbox);
  const unread = useMail((s) => s.unread);
  const opened = useMail((s) => s.opened);
  const draft = useMail((s) => s.draft);
  const notice = useMail((s) => s.notice);
  if (!open) {
    return null;
  }

  const mail = useMail.getState();
  return (
    <WindowFrame
      id="mail"
      title="Mail"
      extra={<span className="mail-unread">{unread > 0 ? `${String(unread)} unread` : ''}</span>}
      onClose={mail.close}
      ariaLabel="Mail"
      className="mail"
      minW={420}
      minH={240}
      initial={(vw, vh) => ({ x: Math.round(vw / 2 - 330), y: Math.round(vh * 0.2), w: 660, h: 420 })}
    >
      <div className="mail-layout">
        <div className="mail-list">
          <div className="mail-tools">
            <button
              type="button"
              onClick={() => {
                mail.write();
              }}
            >
              Write
            </button>
            <button
              type="button"
              onClick={mail.removeRead}
              disabled={inbox?.some((m) => m.read) !== true}
              title="Delete every mail you have read"
            >
              Delete read
            </button>
          </div>
          {inbox === null ? (
            <div className="hint">Reading your mail…</div>
          ) : inbox.length === 0 ? (
            <div className="hint">No mail. A full extractor, a sale, a player writing to you: it arrives here.</div>
          ) : (
            <ul className="mail-rows" aria-label="Inbox">
              {inbox.map((m) => (
                <li key={m.id}>
                  <button
                    type="button"
                    className={['mail-row', m.read ? '' : 'unread', opened?.id === m.id ? 'selected' : '']
                      .join(' ')
                      .trim()}
                    onClick={() => {
                      void mail.show(m.id);
                    }}
                  >
                    <span className="mail-row-from">{senderOf(m)}</span>
                    <span className="mail-row-age">{ageText(m.ageS)}</span>
                    <span className="mail-row-subject">{m.subject}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
        <div className="mail-pane">
          {draft !== null ? (
            <Composer draft={draft} notice={notice} />
          ) : opened !== null ? (
            <Reader mail={opened} />
          ) : (
            <div className="hint">{notice ?? 'Pick a mail to read it.'}</div>
          )}
        </div>
      </div>
    </WindowFrame>
  );
}

function Reader({ mail }: { readonly mail: MailFull }) {
  const store = useMail.getState();
  return (
    <article className="mail-reader" aria-label={mail.subject}>
      <div className="mail-reader-head">
        <div className="mail-reader-subject">{mail.subject}</div>
        <div className="mail-reader-from">
          {senderOf(mail)} · {ageText(mail.ageS)}
        </div>
      </div>
      <div className="mail-reader-text">{mail.text === '' ? <span className="hint">No text.</span> : mail.text}</div>
      <div className="mail-tools">
        {mail.kind === 'player' && mail.from !== '' ? (
          <button
            type="button"
            onClick={() => {
              store.write({
                to: mail.from,
                subject: mail.subject.startsWith('Re: ')
                  ? mail.subject
                  : `Re: ${mail.subject}`.slice(0, MAIL_SUBJECT_MAX),
              });
            }}
          >
            Reply
          </button>
        ) : null}
        <button
          type="button"
          className="danger"
          onClick={() => {
            store.remove(mail.id);
          }}
        >
          Delete
        </button>
      </div>
    </article>
  );
}

function Composer({ draft, notice }: { readonly draft: MailDraft; readonly notice: string | null }) {
  const store = useMail.getState();
  const sending = useMail((s) => s.sending);
  const bytes = utf8Length(draft.text);
  return (
    <form
      className="mail-composer"
      onSubmit={(e) => {
        e.preventDefault();
        void store.send();
      }}
    >
      <label>
        To
        <input
          type="text"
          value={draft.to}
          autoFocus={draft.to === ''}
          placeholder="A player's name"
          onChange={(e) => {
            store.setDraft({ to: e.target.value });
          }}
        />
      </label>
      <label>
        Subject
        <input
          type="text"
          value={draft.subject}
          autoFocus={draft.to !== ''}
          maxLength={MAIL_SUBJECT_MAX}
          onChange={(e) => {
            store.setDraft({ subject: e.target.value });
          }}
        />
      </label>
      <textarea
        className="mail-text"
        value={draft.text}
        rows={8}
        aria-label="The text"
        onChange={(e) => {
          store.setDraft({ text: e.target.value });
        }}
      />
      <div className="mail-tools">
        <span className={bytes > MAIL_TEXT_MAX ? 'mail-bytes over' : 'mail-bytes'}>
          {bytes} / {MAIL_TEXT_MAX}
        </span>
        {notice === null ? null : (
          <span className="hint" role="status">
            {notice}
          </span>
        )}
        <button type="submit" disabled={sending}>
          {sending ? 'Sending…' : 'Send'}
        </button>
        <button
          type="button"
          onClick={() => {
            useMail.setState({ draft: null, notice: null });
          }}
        >
          Cancel
        </button>
      </div>
    </form>
  );
}
