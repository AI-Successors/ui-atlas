# Architecture and data flow

The repository uses small projects so capture, untrusted-input parsing, canonical construction, persistence, reading, and presentation remain independently testable.

```text
selected HWND + explicit consent
  -> scoped Win32/input/window-pixel capture
  -> killable bounded UI Automation helper
  -> sealed .mlrec bundle (raw + rebuildable derived + hashes)
  -> strict validation
  -> per-observation Raw Data Streams materialization
  -> deterministic Raw World materialization
  -> deterministic Semantic World projection + lineage
  -> integrity validation
  -> atomic SQLite publication
  -> human-readable windows/variants/controls JSON map
  -> optional canonical SQLite or version-pinned flat UiAtlas publication
  -> separate privacy-safe graph interchange
  -> independent reader / read-only explorer
```

`UiAtlas.Core.Contracts` contains only versioned data contracts. `Recording` owns safe bundle ingestion and finalization. `Recording.Windows` owns attended desktop collection. `Build` preserves provider-reported Raw Data Streams, resolves bounds-and-ancestry-backed effective ownership for Raw World, fuses durable surfaces and controls, materializes variants and transitions, and projects a lineage-backed Semantic World. `Storage` owns hostile-input-aware SQLite, the human windows/variants/controls projection, lower-level graph interchange, the local catalog, and the isolated flat compatibility publisher. `Reader` has WPF-free three-level AppMap/topology, surface-local projection, control-crop and stacking policy, deterministic primary-lineage rows, higher-world observation-curation, query, filter, hierarchy, and diff models. `Desktop` binds those policies to the focused read-only editor. `Cli` is orchestration only.

Canonical identity never uses HWND, PID, timestamps, absolute paths, or literal window titles. Those values remain evidence. IDs are versioned SHA-256-derived tokens over normalized structural fields. Collection order is explicitly sorted before construction. The build timestamp comes from the sealed source manifest so repeated builds have the same semantic hash.

Opaque visual controls use a normalized perceptual fingerprint that excludes absolute coordinates, window dimensions, DPI, and application version. The Raw World retains every unverified visual candidate; the Semantic World classifies its inferred control kind (for example `Button`) and carries forward its extraction sources, confidence, candidate/evidence identifiers, and verification status. Equal-looking siblings are scoped to their semantic surface and disambiguated by stable reading order.

The visual fallback is region-based rather than application-specific. It runs for unresolved containers and divergent accessibility views even when the application exposes other healthy controls, which covers partially opaque ribbon applications such as Revit. Already known accessibility controls mask matching visual rectangles so the fallback fills gaps instead of duplicating controls.

The recording-mode chooser exposes active hover/focus discovery separately from passive visual recognition. Turning the option off prevents synthetic pointer movement and keyboard-focus walking; screenshot-based recovery of missing controls remains enabled. Customer-data capture is an explicit opt-in. Supported source adapters create a checksummed `customers.jsonl` package beside the map from a stable read-only snapshot. Only the package status, source type, record count, package identifier, and SHA-256 enter the UI graph; customer fields never become graph properties.

Raw Data Streams entities are observation-specific and retain the recorded native window/UIA facts before fusion. Raw World identity is based on root-owner lineage, native window class/style, stable accessibility identifiers, control types, hierarchy, and normalized relative geometry. Raw World surfaces cite their source Raw Data Streams surfaces. Accessibility labels are descriptive evidence, never durable identity inputs. Semantic World entities cite their source Raw World identifiers. The compatibility publisher is downstream-only: it cannot alter canonical truth and emits application/window/control authoring records plus bounded schema-5 observation packages containing sanitized Win32/UIA mirrors. It does not emit actions, scopes, machine paths, or runtime policy.

The recorder seals the initially selected HWND, its root-owner HWND, PID, and process start time. Pointer events are accepted only when the pointed window resolves to that root owner; keyboard events are accepted only while it is foreground. After one full baseline, ordinary clicks are event-only. First visits to discovered Ribbon/navigation tabs and material changes to the main surface enqueue bounded full-root observations. A process-scoped WinEvent monitor enqueues visible owned popups without blocking input. Each candidate is inspected only inside its HWND and committed as one transaction: UIA snapshot A, popup PNG, then UIA snapshot B. The transaction is retained only when bounds and connected interactive structure agree; worksheet contamination, incomplete candidates, and structural duplicates are rejected before persistence. The queue has one consumer and a two-second Finish boundary. Printable key identities and edit/document accessibility names are redacted during collection.

The CLI catalog stores recordings, maps, safe exports, and recoverable deletions under the current user's local application data directory. Catalog identifiers are filename-safe opaque slugs and are resolved with containment checks. `recording start` seals the bundle and then builds the corresponding map with the same identifier; no workspace or project registration participates. `UI-ATLAS_DATA_HOME` exists only as a test and automation override for the catalog root.

The main SQLite artifact is published through a temporary file, one transaction, integrity checks, and an atomic same-volume move. Readers open it read-only with pooling disabled, trusted schemas disabled, schema allowlisting, row and payload caps, and integrity checks.

`UiEvidenceReader` is WPF-free and opens only a fully validated recording bundle. It binds graph evidence to the exact observation/frame pair and returns bounded PNG bytes plus frame-relative bounds. The desktop layer alone decodes those bytes and draws the highlight; graph read models do not depend on WPF or image loading.
