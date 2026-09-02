# Interaction Trace

UiAtlas UIKG v4 records causal UI navigation as `state → control → action → result state`.
Each `raw/interactions.jsonl` item belongs to one recording session and preserves its operation ID,
attempt, sequence, actor, gesture, semantic action, source evidence, result frames, outcome, and a
bounded diagnostic code. Typed text is never stored.

Successful observations become `interaction` edges. `NoChange`, `Failed`, and `TimedOut` observations
remain self-loop negative examples. UIA affordances are possible actions only: an unobserved affordance
has no invented destination. Resume builds merge equal routes while retaining each session step.

The desktop explorer exposes `Trace` for step-by-step evidence and `Routes` for the merged graph.
`Routes` renders shared states once and expands their controls into connected branches, so a path such
as `Home → Font menu → font popup → selection` or `Home → Format Cells → Font tab` can be inspected
visually. Selecting an observed action opens its source/result evidence in `Trace`. Solid branches are
observed, while dashed branches are affordances whose destination is intentionally unknown.

Automatic navigation also recognizes traditional Win32 application menu bars. It may safely expand
top-level menus such as `File`, `Edit`, and `Window` to capture their contents, but it does not execute
the commands inside those menus.
Legacy graphs remain readable and display `Trace unavailable`; temporal proximity is never used to
invent a causal link.

Human-readable exports default to `ui-atlas.map.json/2` and contain `interactionTrace`, `routeGraph`,
`affordances`, and `negativeExamples`. Version 1 exports remain readable.
