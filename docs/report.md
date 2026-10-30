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
| **Look** | After it settled: does something cover the page (an element over most of the viewport that the page did not ask for), is the visible text in the script the caller expects, does the visible text stand out from what is behind it, does every field have a name? | The loaded page. |
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
| `text-contrast` | Visible text does not stand out enough from what is behind it (WCAG 2.x: 4.5:1, 3:1 for large text). Text over a picture or a gradient is skipped, not guessed. | `selector`, `text`, `foreground`, `background`, `ratio`, `needed`, `count` (elements with the same pair) |
| `unnamed-field` | A visible input, select or text area has no name a screen reader can read (no label, `aria-label`, `aria-labelledby` or `title`; a placeholder counts for inputs). | `selector`, `nearText`? (the text beside it, for choosing a name) |
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

## The context a run states

A page behaves differently in the window a person uses than in a driven browser, and a check that passes in the second can hide a defect
in the first (a service worker that freezes a page at its first version is invisible to a driver that blocks workers; a policy the driver
does not apply lets a page through that the window would break). page-assay therefore (a) **states the context it ran in** and (b)
**follows the caller's real window wherever it can**, and says where it cannot.

The report carries a `context` block:

```jsonc
"context": {
  "engine": "Chromium 130.0",         // name and version of the driven browser
  "headless": true,
  "origin": "http",                    // "http" (a page served on an origin of its own) or "file" — never silently the second
  "policy": "sha256:…",                // the response policy the page was served under, or null when none
  "serviceWorkers": "allow",           // "allow" or "block"
  "viewports": [ { "width": 1280, "height": 800, "scale": 1 } ],
  "colorSchemes": [ "light", "dark" ],
  "returningVisit": true               // the page was loaded a second time in the same storage
}
```

| Aspect | Stance |
|---|---|
| **Origin** | An origin of its own, as the person's window has; `file:` is a different origin kind (module scripts, `fetch` of a relative file, root-relative URLs and storage all behave differently) and is reported as such, never used silently in its place. |
| **Response policy** | An **input from the caller**. page-assay carries no copy of any product's policy: a hand-copied policy drifts, and the drift is felt as a page that passes here and breaks there (or the reverse). The report names the policy it ran under. |
| **Service workers** | Allowed by default. A page that registers one is a finding (`service-worker`): a cached page the person cannot tell is stale is a defect no later check can see. A caller whose window forbids workers says so, and the run blocks them. |
| **Network** | None beyond the page's own origin; every other request is refused and listed as `blocked`. |
| **Storage** | A fresh profile, and — on request — a second load in the same storage (`returningVisit`), which exercises the paths a first visit never takes. |
| **Viewport and colour scheme** | A list the caller supplies; the default is the caller's window size and one narrower, in light and dark. An unreadable dark page or a layout broken at a narrower width is a common defect of generated pages. |
| **Navigation away** | Reported (`navigated`, with the target), not followed — a window that cancels it will not show what the driver would have followed. |
| **Dialogs** | Answered the way the caller says and listed; a person may answer otherwise. |
| **Engine, headless, GPU** | A known limit: stated in `context`, not emulated. A page that needs the GPU is not proven by a headless run. |

## Where findings come from

The browser half collects errors through the page's origin rather than re-implementing collection: a local-origin preview origin
already places a problem-report script in front of the page and maps lines to the page's own file. page-assay reads those reports
and adds what only a driver sees (settling, covering, language, text contrast, unnamed fields, interaction, resources that never arrived). local-origin's
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
6. **A context it cannot match.** When the caller asks for a context the driver cannot give (a different engine, a real GPU), does the run fail, or run and state the difference in `context`? Proposal: run, and state it — the caller decides whether the difference matters.
7. **Where the policy comes from.** The caller passes the policy it serves with (as headers, or a reference the origin library can resolve). Whether page-assay reads it from the origin it drives or takes it as a parameter depends on the first open question above.
