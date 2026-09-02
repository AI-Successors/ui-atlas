# Three-application proof video

The proof video should be a short screen recording, not an animated product claim. Target length: 75–90 seconds. Audience: Windows QA, RPA, and computer-use agent developers. Desired action: download the release ZIP and reproduce one map.

## Scene sequence

1. **Line-of-business application (30 seconds):** show the source screen, start Manual capture, click through three visibly different screens, finish the map, run `MAP QUALITY`, then show the top buttons and structured grid cells in the explorer.
2. **Excel (20 seconds):** capture one worksheet and one Ribbon/menu change, then show headers, rows, cells, and the successful interaction route.
3. **A third complex app (20 seconds):** capture a popup or owner-drawn panel to demonstrate that fallback behavior generalizes across applications.
4. **Integration (10 seconds):** run `UiAtlas.Core.Consumer.dll <map> --query <control> --json` and highlight the returned selector/action/target-state fields.
5. **Close (5 seconds):** show the local data location and the statement “offline, local, no telemetry.”

Keep the recorder timer and stage indicator visible. Do not cut away during a long scan; the elapsed time is part of the evidence. If `MAP QUALITY` says `NEEDS REVIEW`, show the reason and repeat that application run before publishing the video.
