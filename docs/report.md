# The assay report

> Status: the contract of what page-assay returns, and the record of what is built. **Built:** the static rules (all six kinds
> below), load, look (covering, contrast, unnamed fields, table shape, overflow) and comparison of two versions. **Not built yet:**
> `language`, `interaction` and the interact steps, `service-worker`, `navigated`, `afterDeclinedCall`, `excerpt`, and the
> `policy` / `serviceWorkers` / `returningVisit` context. The open questions are at the end.

page-assay checks a web page without AI and returns **data**: a list of findings a program can act on — show a person what is
wrong, hand the findings to whatever repairs the page, or decide that a page is good enough to publish. Every finding carries a
stable `kind` code, the fields that kind needs, and one plain English line (`text`) generated from those fields by a fixed
template — deterministic, the same input always giving the same line, so a repair loop can hand it to a model as it is. The line
is a convenience, never the contract: a caller that shows findings to people writes its own wording from `kind` and the fields.

## What a run looks at

| Part | What it does | Needs |
|---|---|---|
| **Static rules** | Read the page's source without running it: the syntax of every inline script and event-handler attribute, names called but defined nowhere, elements looked up by an id the markup does not have, an id used twice, names read while the page starts before they have a value, and code, files or styles that would reach another host. See «The static rules». | The page's HTML. Inline scripts only — a script loaded from a file is not read. |
| **Load** | Open the page in a browser driven over the Chrome DevTools Protocol, wait until it has **settled**, and collect what went wrong while it loaded. | A page served on an origin of its own (for example a [local-origin](https://github.com/iyulab/local-origin) preview origin) and a browser: one page-assay launches (headless Edge or Chrome), or **an existing CDP endpoint the caller already owns** (an embedded WebView2, a browser the caller started). |
| **Look** | After it settled: does something cover the page (an element over most of the viewport that the page did not ask for), is the visible text in the script the caller expects, does the visible text stand out from what is behind it, does every field have a name? | The loaded page. |
| **Interact** | Optional steps the caller supplies — press this, type that, expect this — each judged by what the page shows afterwards. A step that does nothing observable is a finding (a page can fail silently: a button that does nothing raises no error). | Steps from the caller. Their vocabulary is a later design. |

## The report

```jsonc
{
  "page": "notes/index.html",          // the caller's name for the page
  "loaded": true,                       // false when the page never finished loading; absent when the page was not driven
  "settledMs": 840,                     // how long until the page settled (see "Settled"), or null when it never did; absent
                                        // when the page was not driven
  "findings": [ /* Finding, in the order they happened */ ],
  "truncated": false                    // true when more findings happened than the report keeps
}
```

### Severity and limits

Every finding has a `severity`: `block` (the page is broken for a person — it does not run, a control does nothing, text cannot
be read) or `notice` (worth repairing, not a reason to refuse the page). Each kind has a **default** severity, listed below; the
caller can override it per kind, because what blocks publishing is the caller's policy, not the layer's.

A run keeps at most a few findings **of the same kind** (default 3, an option) and adds one `more` entry per kind that was cut:
`{ "kind": "more", "of": "text-contrast", "count": 14 }`. A repair loop reads three examples better than seventeen, and the
count keeps the size of the problem visible.

### Finding

Every finding has a `kind` and the fields that kind needs. Fields absent from a kind are omitted, never `null`.

| `kind` | Default severity | When | Fields |
|---|---|---|---|
| `syntax` | block | An inline script or event-handler attribute does not parse. External `src` scripts and blocks whose `type` is not JavaScript are not read; a `type="module"` script parses as a module. | `source` (`script N` — the N-th `<script>` element of the page — or `onclick attribute of <selector>`), `line`, `column`, `message` (the parser's, without a position), `hides`? (see below) |
| `undefined-name` | block | A name is used — called (`f()`, `new F()`) or read as a value (`button.onclick = downloadCsv`, `show(rows)`) — but defined nowhere the use can reach; the use throws a ReferenceError. An `onclick="save()"` with no `save` (`reason`: `undefined`), or a name that exists only where the use cannot reach it: inside a function, or at a module script's top level, while a handler attribute or another script uses it as a global (`not-global`). A write (`x = 1` makes a global) and `typeof x` are not uses; a value read of a name the markup gives (an element id, a `name` attribute) or of `arguments` finds a value and is not reported. One name is one finding, at its first use. | `name`, `use` (`call` or `read`), `reason`, `line`, `column` |
| `missing-id` | block | Code looks an element up by an id the markup does not have and the code never gives an element (`document.getElementById("x")`, `document.querySelector("#x …")`, `document.querySelectorAll("#x …")`, with a literal). | `id`, `line`, `column` (of the literal) |
| `duplicate-id` | notice | Two or more elements in the markup carry the same id. | `id`, `count`, `lines` (each element's line, in document order) |
| `outside-reference` | block | Code or markup would reach another host, which a page meant to run without the network cannot. **One finding per host per rule** (seventeen images from one host are one finding, at the first). | `rule`, `url`, `code`, `line`, `column` — see «Outside references» |
| `read-before-set` | block | The page reads a name while it starts, before the line that gives it a value, and that throws — a function called at the top of a script that reads a `const` declared below the call. Under `let`/`const`/`class` (or a name a later script declares) the read throws a ReferenceError; under `var` the name is `undefined`, which throws a TypeError only where it is called, constructed or has a member read — a plain value read (`clearTimeout(timer)`) is harmless and not reported. A `try` around it does not hide it: the page then runs without what the code meant to set up. | `name`, `call`? (the call made by the starting code that led to the read; absent when the starting code read the name itself), `line`, `column` (the read), `setAt` (the line that gives it a value), `error` (`ReferenceError` or `TypeError` — what the browser throws) |
| `uncaught` | block | The page's own code threw and nothing caught it. | `message`, `file`? (when not the page itself), `line`, `column`, `phase` |
| `rejection` | block | A promise the page made was rejected and nothing handled it. | `message`, `line`?, `column`?, `phase` |
| `console-error` | notice | The page reported an error itself with `console.error`. | `message`, `phase` |
| `error-on-screen` | block | The visible text holds a JavaScript engine's error sentence (`Cannot access 'x' before initialization`, `… is not a function`): the page caught an exception and printed its message, so no error report and no console line exists. | `selector`, `text`, `phase` |
| `resource` | block | A file the page asked for from its own origin did not arrive (missing, server error, a dropped connection). Requests the browser makes on its own (a favicon) are not the page's and are not reported. | `url`, `status`? or `error`? |
| `blocked` | block | Something was refused: by the content security policy (`library` · `data` · `form`), or by the run itself because it went to another host (`network`). | `category`, `host`, `url`? |
| `covering` | block | An element the page marked `[hidden]` is shown anyway (not `display:none`, not `visibility:hidden`, not `pointer-events:none`) and covers at least half of the window — a generated overlay overriding `[hidden]`, with nothing under it usable. A full-page app root is never `[hidden]`, so it never fires on one. | `selector`, `display`, `area` (fraction of the viewport) |
| `table-shape` | notice | A table whose rows do not have as many cells as its header has columns (spans counted; a table with a row span is skipped): values sit under the wrong headers with no error. Limit 2 per page by default. | `selector`, `columns` (header names), `rows` (how many are off), `cells` (the counts seen) |
| `overflow` | notice | At one of the run's viewports the page is wider than the window, so a person has to scroll sideways. | `by` (px), `viewport`, `scheme` |
| `language` | notice | The visible text is mostly in another script than the caller expects. | `expected`, `observed`, `ratio` |
| `text-contrast` | notice | Visible text does not stand out enough from what is behind it (WCAG 2.x: 4.5:1, 3:1 for large text), measured on the composited colours, over the page's canvas as its colour scheme resolves it. Looked at in every viewport and colour scheme of the run; the same colour pair is one finding, reported with the `scheme` and `viewport` it was first seen in. An empty field is measured with its `::placeholder` colour; transparent text is skipped; text over a picture or a gradient is skipped, not guessed. Findings with the same colour pair are one finding. | `selector`, `text`, `foreground`, `background`, `ratio`, `needed`, `count` (elements with the same pair) |
| `unnamed-field` | notice | A visible input, select or text area has no name a screen reader can read (no label, `aria-label`, `aria-labelledby` or `title`; a placeholder counts for inputs). | `selector`, `nearText`? (the text beside it, for choosing a name) |
| `interaction` | block | A caller's step did not have its expected effect. | `step` (the step's index), `expected`, `observed` |

A broken script is reported **once, as its cause**: when a script does not parse, the names it would have defined are not also
reported as `undefined-name` — one `syntax` finding, not seventeen calls to functions that "do not exist". The suppressed findings
are counted in the cause's `hides` field.

### The static rules

The static rules read the page's HTML as written (line breaks of any kind count once): every inline `<script>` of a JavaScript type
and every `on…` attribute. A script with a `src` is not read, and a `<template>`'s content does not run, so it is not read either.

- **Globals.** Classic scripts share one global scope: their top-level `var`, `let`, `const`, `function` and `class` names are
  globals, reachable from later scripts and from handler attributes. So are names assigned to `window.x`, `globalThis.x`,
  `self.x` (or `window["x"]`), and names assigned without a declaration. A module script's top level is its own. The browser's
  own names (`document`, `fetch`, `JSON`, `setTimeout`, constructors…) are a list generated from a current Chromium
  (`tools/browser-names.html`). Element ids are not assumed to be globals.
- **Handler attributes** run as functions whose scope reaches, before the globals, their element, its form and the document: a
  handler may call `submit()`, `focus()` or `getElementById(…)` unqualified, and may `return`.
- **What turns `undefined-name` off.** A page that loads a script from a file — a `src`, or a module an inline module imports — (its
  names are unknown), and code that uses `eval` or `with` (what a name refers to cannot be read from the source), are not checked
  for it; a name the page tests with `typeof` or reads as `window.x` is one it knows may be missing, and is not reported. The
  handler scope is the union of what common elements, a form and the document offer, so a handler is never faulted for a name
  its own element might have.
- **Ids the code makes.** `missing-id` counts an id as present when the code gives one — `el.id = "x"`, `setAttribute("id", "x")`,
  `id="x"` in a string or template the code holds (markup it inserts), or the literal start of one (`"row-" + n` covers `row-3`) —
  and the ids inside a `<template>`, there once the code clones it in (they are not counted by `duplicate-id`).
  When the code gives an id the source does not spell out (`el.id = f()`), the rule does not run.
- **The order `read-before-set` follows** — what runs while the page starts: the top level of classic scripts in document order
  (functions declared in a script have their value from its start — a function declared later in the same script but only called
  later is never reported; a script's names do not exist before it runs), then module scripts, then the `DOMContentLoaded` and
  `load` listeners registered on `document`/`window` (and `window.onload`, `<body onload>`), which run after all of it. Inside it,
  functions run at once (an IIFE, `f.call(…)`) and calls to a function whose value the source states (a declaration, or
  `const f = () => …` once it is set) are followed. What is **not** followed, so as never to report a read that does not happen:
  callbacks handed to other code (timers, other events, array methods), an async function past its first `await`, generator
  bodies, a `catch` block (it runs only when its `try` fails), the branches of a condition that tests `typeof` or
  `document.readyState` (an inline script always runs while the document loads, so «wait if loading, else run now» never runs
  now); and a `var` read by its own initializer (`var x = x || {}`) or never given a value at all is not reported. Both branches of
  any other condition are followed, and a throw on one does not hide the other. **A read that throws ends its path**, as in the
  page: the rest of the function and its callers up to the nearest `try` (or the end of the script) do not run, so one defect is
  one finding — a second read after it is never reached.

A broken script is reported **once, as its cause**: when a script does not parse, the names it would have defined are not also
reported as `undefined-name` — one `syntax` finding, not seventeen calls to functions that "do not exist". The names it would
have defined are read from its text (`function f`, `class C`, `var`/`let`/`const x`, `window.x =`), and the suppressed findings
are counted in the cause's `hides` field (absent when none).

### Outside references

A URL is outside when it is absolute (`http:`, `https:`, `ws:`, `wss:`) or protocol-relative (`//host/…`) and not on the page's
own origin. Relative URLs, `data:`, `blob:`, `#…`, `mailto:` and the like are not; neither is a bare module specifier. A static-only
run that is not told the origin the page will be served on counts every absolute URL as outside.

| `rule` | What | `code` |
|---|---|---|
| `script-src` | `<script src>` | true |
| `fetch` | `fetch(…)` (also `window.fetch`) with a literal URL; a template literal is judged by its literal start | true |
| `import` | static `import … from`, `export … from`, and dynamic `import(…)` with a literal | true |
| `link` | `<link href>` whose `rel` fetches: `stylesheet`, `icon`, `apple-touch-icon`, `manifest`, `preload`, `prefetch`, `modulepreload` (`canonical`, `alternate`, `preconnect` and the like fetch nothing) | true for `modulepreload`, and for `preload`/`prefetch` of a script or worker; false otherwise |
| `form-action` | `<form action>`, `formaction` on a button or input | false |
| `img` | `<img src>`, `<img srcset>` (each candidate), `<input type=image src>` | false |
| `media` | `<video src/poster>`, `<audio src>`, `<source src/srcset>`, `<track src>` | false |
| `frame` | `<iframe src>`, `<frame src>` | false |
| `object` | `<object data>`, `<embed src>` | false |
| `css-url` | `url(…)` in a `<style>` block or a `style` attribute (comments skipped) | false |
| `css-import` | `@import` with or without `url(…)` | false |

`code` says what fails when the host is unreachable: `true`, code the page runs does not arrive (the page may not work at all);
`false`, a file, a style or a form target does not (the page runs, without it).

A driven run reports the same request again when the page makes it (`blocked`): the static finding says the source asks for it,
the driven one that the request was made and refused. The two are not merged — nor are a `syntax` finding and the driven
`uncaught` SyntaxError of the same script, or an `undefined-name` and the ReferenceError it throws in use.

Shared fields:

- **`text`** — the one-line English summary described at the top, from a fixed template per kind.
- **`severity`** — `block` or `notice` (see «Severity and limits»).
- **`key`** — the finding's identity across two versions of a page (see «Comparing two versions»).
- **`deferred`** (`true` or omitted) — in a comparison whose new version has more `syntax` findings than the old, every other finding: nothing else measured on a version whose scripts do not parse can be trusted. They keep their status, for a caller that drops or shows them.
- **`status`** — `new`, `kept` or `gone`, only when two versions were compared. The per-kind limit applies per kind *and* status, so the findings a change caused are never crowded out by old ones.

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

## Comparing two versions

A repair loop needs to know what **this change** broke, not every finding the page has ever had: a model that is handed old
findings with new ones "repairs" what it was not asked to touch. So comparison is first-class: a run can take a page's previous
version as well, and every finding is then marked `new` (only in the new version), `kept` (in both) or `gone` (fixed by the change,
listed once with `status: "gone"`).

Two findings are the same finding when their `key` is equal. The key is built from what survives an edit elsewhere in the file —
never the line number, never a script's position among the page's scripts: the kind, plus the name, id, selector, URL or colour
pair the kind is about. A line moved by an insertion above it keeps its key, and so does a script when another is inserted above it.
The static kinds:

| `kind` | Key |
|---|---|
| `syntax` | the script's content (a hash of its text), or the handler attribute: its element's id, else its label (`aria-label`, `title` or its text), else — only when it has neither — its selector, plus the event (`onclick`) |
| `undefined-name` | the name and the reason, plus the handler attribute (as above) when a handler uses it |
| `missing-id`, `duplicate-id` | the id |
| `outside-reference` | the rule and the host (not the URL: a different first image from the same host is the same finding) |
| `read-before-set` | the call and the name |

The same key twice in one version is one finding, reported where it is first seen.

Comparison covers **both** halves: the static rules compare the two sources directly; the driven run loads each version in the
same context. A caller that only needs the static half (cheap, no browser) can ask for that alone: `Assayer.RunStatic` reads one
source, `Assayer.CompareStatic` compares two, with the same limits, statuses and `deferred` marking as a driven comparison. A driven
run reads the page's source too — the document the browser received, minus the `PageLineOffset` lines in front of it — and its
static findings come first in the report (option `StaticRules`, on by default).

## The context a run states

A page behaves differently in the window a person uses than in a driven browser, and a check that passes in the second can hide a defect
in the first (a service worker that freezes a page at its first version is invisible to a driver that blocks workers; a policy the driver
does not apply lets a page through that the window would break). page-assay therefore (a) **states the context it ran in** and (b)
**follows the caller's real window wherever it can**, and says where it cannot.

The report carries a `context` block:

```jsonc
"context": {
  "static": true,                      // the static rules read the page's source
  "driven": true,                      // the page was loaded in a browser; when false, the browser's fields below are absent
  "engine": "Chromium 130.0",         // name and version of the driven browser
  "headless": true,
  "origin": "http",                    // "http" (a page served on an origin of its own) or "file" — never silently the second
  "policy": "sha256:…",                // the response policy the page was served under, or null when none
  "serviceWorkers": "allow",           // "allow" or "block"
  "viewports": [ { "width": 1280, "height": 800, "scale": 1 } ],
  "colorSchemes": [ "light", "dark" ],
  "returningVisit": true,              // the page was loaded a second time in the same storage
  "templates": "1"                     // the version of the one-line text templates (see open question 9)
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

page-assay collects everything through the driver itself: errors and rejections from the runtime's exception events, console errors,
requests and responses, and what the look checks measure in an isolated world the page cannot see. It needs nothing from the origin
that serves the page beyond the page itself — any origin works, a local-origin preview origin included.

One thing only the serving origin knows: whether it put markup in front of the page (a preview origin injects its problem-report and
key scripts). The caller passes that as `PageLineOffset`, and lines in the page's own file are reported minus it. An error inside the
injected part carries `injected: true` and its raw line.

## Open questions

1. ~~Problem reports carry their kind and position as fields~~ — no longer needed by page-assay: it reads the exception events directly
   (see «Where findings come from»). A change to local-origin's wire stays that library's own question.
2. ~~A settled signal from the origin, or from the driver?~~ — the driver: no pending request, and no document change for a quiet
   period, measured in an isolated world.
3. ~~Reading reports while the page runs~~ — events arrive as they happen over the driver.
4. **Limits.** Per kind (default 3, per comparison status) with a `more` count, and a total (default 50) with `truncated`.
5. **Interaction steps** — their vocabulary is the next design, after the report settles.
6. **A context it cannot match.** When the caller asks for a context the driver cannot give (a different engine, a real GPU), does the run fail, or run and state the difference in `context`? Proposal: run, and state it — the caller decides whether the difference matters.
7. **Where the policy comes from.** The caller passes the policy it serves with (as headers, or a reference the origin library can resolve). Proposal: a parameter — page-assay reads nothing else from the origin (see «Where findings come from»).
8. **The keys of driven findings.** Built so far: `text-contrast` keys on its colour pair and threshold, `unnamed-field` on the text
   beside it (its selector only when there is none), `error-on-screen` on selector and text. A generated page often has no stable ids,
   so a positional selector can shift when an element is inserted before it. Proposal for the rest: the nearest id or label plus the
   element's text, the positional selector only when neither exists.
9. **The template wording of `text`** is part of what a caller may rely on (a model is prompted with it). Proposal: changing a
   template is a minor version, listed in the changelog.
