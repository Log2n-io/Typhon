# Doc-fix rules — binding for the doc-accuracy workflows

Read by the nightly review (`doc-accuracy-review.yml`) and the weekly audit (`doc-accuracy-audit.yml`) before
they edit any documentation page. One copy, so the two prompts cannot drift apart on how a fix is made.

- **HARD CONSTRAINT: edit files under `doc/` ONLY.** Never touch `src/`, `.github/`, `scripts/`, `coverage/`, or
  anything else — a later step diffs the tree and FAILS THE RUN if you did. Do not run git. Do not commit. Do not
  push. Something else does that.

- **Fix ONLY what you are confident about.** A finding you are sure is wrong but unsure how to phrase should be
  filed as an issue and left unedited — an issue costs a read, a wrong committed sentence costs a reviewer who
  believes it. Partial coverage is expected and fine.

- **QUALIFY, DO NOT REPLACE.** When code gained an opt-in mode and the page describes the old behaviour, that prose
  is still exactly right for the default — say so, then add what the new mode does instead. Rewriting it makes the
  path 100% of users are on read as the exception.

- **State the exemptions, not just the headline.** A rule with carve-outs documented as absolute teaches readers to
  expect the wrong thing in precisely the cases that bite.

- **VERIFY THE WHOLE PARAGRAPH YOU EDIT,** not only the sentence you came for. A paragraph is one unit to a reader,
  so rewriting half of it and inheriting the rest unchecked signs your name to claims you never read. This has
  already caused drift: the 2026-08-01 run rewrote the "PK fallback" paragraph in `09-querying.md` and inherited a
  stale cluster-storage sentence beside it, which the next night had to file again as #642.

- **NEVER write a linking phrase** — "in either case", "likewise", "the same applies", "in both paths" — into prose
  you have not verified THIS run. A connector widens the scope of the claim it points at; widening an unverified
  claim is precisely how a fix manufactures new drift. That is the exact mechanism behind #642: "In either case ..."
  was added in front of a sentence that was only ever true for non-cluster archetypes, and the connector made it
  read as universal.

- **If a neighbouring claim is wrong and you are not confident how to fix it, SPLIT the paragraph** instead of
  bridging across it, and file the remainder as its own issue.

- **The same fact often lives on several pages.** Before finishing a fix, grep `doc/` for the wrong value or name
  and fix every occurrence you can verify — a finding corrected on one page and left on its siblings is refiled the
  next week.
