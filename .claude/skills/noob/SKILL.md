---
name: noob
description: Toggle "noob mode" — explain Typhon to someone who has never seen it, in short plain English with zero assumed knowledge
argument-hint: "on | off"
---

# Noob Mode

A conversation register switch. It changes **how** you answer, not **what** you work on.

## Input

`$ARGUMENTS` is `on` or `off`. If empty, assume `on`.

## When `on`

Reply exactly: `🟢 Noob mode ON — plain English, no assumed knowledge.` Then answer whatever was asked, in the mode.

Stay in the mode for the rest of the session, or until `/noob off`.

### Rules

1. **Assume the reader has never used Typhon** and does not know its concepts, type names, file layout, or history.
2. **Assume general programming literacy.** They know what a database, a thread, a file, and a crash are. Don't explain those.
3. **Short.** Aim for 3–8 lines. A table or a 3-item list beats a paragraph. Hard ceiling ~15 lines unless they ask for more.
4. **Plain words first.** If a Typhon term is unavoidable, define it inline in ≤1 clause the first time:
   *"an archetype (Typhon's word for a kind of object, like `Player`)"*.
   Never leave a term undefined. Glossary: `claude/design/glossary.md`.
5. **No internals unless asked** — no `file.cs:123`, no class names, no rule IDs (CK-05, RB-04…), no acronyms (MVCC, WAL, SoA, ACW). If one is essential, spell it out once in words.
6. **Concrete over abstract.** "You'd lose the last 8 milliseconds of changes" beats "the durability window widens."
7. **Analogies are welcome** when they genuinely shorten the explanation. Drop them if they don't.
8. **Still be honest.** Simplify, never fudge. If the true answer is "it depends", say so in one line and give the common case. Say "I don't know" rather than inventing a simple story.
9. **Answer the question asked.** Don't add background they didn't request.

### What does NOT change

- Never commit code; the user does that.
- A question is a question — answer it, don't start editing files.
- Facts still come from the docs and the code, not from memory or guesses.

## When `off`

Reply exactly: `⚪ Noob mode OFF — back to normal.` Resume the usual expert register: precise terms, `file:line` citations, internals, full technical depth.

## Note on persistence

This is a context-level switch, not stored state. It holds for the current session. A fresh session starts in normal mode — run `/noob on` again.
