# The assay report (draft)

> Status: **draft for review** — the first design of what page-assay returns. Nothing implements it yet. Comments on the
> shape are welcome before the first code lands; the open questions are at the end.

page-assay checks a web page without AI and returns **data, never prose**: a list of findings a program can act on — show a
person what is wrong, hand the findings to whatever repairs the page, or decide that a page is good enough to publish. Wording
for people is the caller's; page-assay never writes a sentence meant for a person to read.

## What a run looks at

| Part | What it does | Needs |
|---|---|---|
| **Static rules** | Read the page's source files without running them: code or data that would reach another host (`<script src>`, `fetch`, dynamic `import()`, `<link>`, form actions), and similar rules that need no browser. | The files. |
| **Load** | Open the page in a browser driven over the Chrome DevTools Protocol, wait until it has **settled**, and collect what went wrong while it loaded. | A page served on an origin of its own (for example a [local-origin](https://github.com/iyulab/local-origin) preview origin) and a browser: one page-assay launches (headless Edge or Chrome), or **an existing CDP endpoint the caller already owns** (an embedded WebView2, a browser the caller started). |
| **Look** | After it settled: does something cover the page (an element over most of the viewport that the page did not ask for), is the visible text in the script the caller expects? | The loaded page. |
| **Interact** | Optional steps the caller supplies — press this, type that, expect this — each judged by what the page shows afterwards. A step that does nothing observable is a finding (a page can fail silently: a button that does nothing raises no error). | Steps from the caller. Their vocabulary is a later design. |

## The report

```jsonc
{
  "page": "notes/index.html",          // the caller's name for the page
  "loaded": true,                       // false when the page never finished loading
  "settledMs": 840,                     // how long until the page settled (see "Settled"), or null when it never did
  "findings": [ /* Finding, in the order they happened */ ],
  "truncated": false                    // true when more findings happened than the report keeps
}
```

### Finding

Every finding has a `kind` and the fields that kind needs. Fields absent from a kind are omitted, never `null`.

| `kind` | When | Fields |
|---|---|---|
| `uncaught` | The page's own code threw and nothing caught it. | `message`, `line`, `column`, `phase` |
| `rejection` | A promise the page made was rejected and nothing handled it. | `message`, `line`?, `column`?, `phase` |
| `console-error` | The page reported an error itself with `console.error`. | `message`, `phase` |
| `resource` | A file the page asked for from its own origin did not arrive (missing, server error). | `url`, `status`? |
| `blocked` | The content security policy refused something. | `category` (`library` · `data` · `form`), `host` |
| `static` | A static rule matched in the source. | `rule`, `file`, `line`, `column`, `excerpt`? |
| `covering` | An element covers the page. | `selector`, `area` (fraction of the viewport, 0–1) |
| `language` | The visible text is mostly in another script than the caller expects. | `expected`, `observed`, `ratio` |
| `interaction` | A caller's step did not have its expected effect. | `step` (the step's index), `expected`, `observed` |

Shared fields:

- **`line` / `column` are counted in the page's own file**, not in the document the browser received — whatever a server put in
  front of the page (scripts, headers) is subtracted. A finding that points at line 40 can be repaired at line 40.
- **`phase`** — `load` (before the page settled: it may not have started at all) or `use` (after: one thing it does is broken).
  The same error means different things in the two phases, and a repair should treat them differently.
- **`afterDeclinedCall`** (`true` or omitted) — the finding happened after a call the checker declined on the page's behalf. A
  checker that runs a page with some calls stubbed or refused (calls to a paid service, a model, a payment provider) causes
  errors the page would not have in use. They are still reported — a page should handle a refusal — but a repair loop must be
  able to tell them apart, so it does not "fix" a refusal the checker made. Every finding after the first declined call carries
  the flag.
- **`excerpt`** — a few lines of source around `line`, when the caller asks for them.

### Settled

A page has **settled** when it has loaded, no request it started is still pending, and nothing has changed in the document for
a short quiet period. A fixed wait after `load` is both too short (a page that fetches its data) and too long (a page that is
done at once); settling is what makes `phase` and `settledMs` mean something.

## Where findings come from

The browser half collects errors through the page's origin rather than re-implementing collection: a local-origin preview origin
already places a problem-report script in front of the page and maps lines to the page's own file. page-assay reads those reports
and adds what only a driver sees (settling, covering, language, interaction, resources that never arrived). local-origin's
`load-error` and `error` become `uncaught` / `rejection` / `console-error` with `phase` `load` or `use`; its `blocked` is `blocked`.

## Open questions

1. **Problem reports carry their kind and position as fields.** Today local-origin's problem-report wire has `load-error`,
   `error` and `blocked`; an uncaught error, a rejection and a `console.error` all arrive as `error`, with the line inside the
   message text. The report above needs `uncaught` / `rejection` / `console-error` and `line` / `column` as separate fields —
   an additive change on local-origin's wire.
2. **A settled signal from the origin, or from the driver?** The origin sees the page's requests; the driver sees the document.
   Proposal: local-origin reports pending requests and quiet time; page-assay combines it with the document's mutations.
3. **Reading reports while the page runs.** A long interaction run should not wait for the end: local-origin's preview report
   needs a cursor ("findings since N") or a stream.
4. **Limits.** local-origin keeps 20 entries per preview; page-assay keeps its own cap (proposal: 50) and sets `truncated`.
5. **Interaction steps** — their vocabulary is the next design, after the report settles.
