# Privacy model

Recording is attended and begins only after visible, explicit consent. The user selects one exact application identity. Capture stops on Done, Cancel, target-identity loss, or unrecoverable failure. Cancel asks whether to retain the partial bundle.

Default collection retains scoped pixels, static accessibility labels, class/control metadata, volatile window evidence, and event timing. It never reads accessibility value patterns or password content. Printable keyboard identities and scan codes are suppressed; literal typed text is not collected. Edit and document accessibility names are replaced at collection time.

Raw bundles are local sensitive evidence. The safe export is the sharing default: it removes free-text labels and properties, source bundle identifiers, screenshot references, raw stable keys, native coordinates, and build time. It generates a fresh identifier namespace on every export so publications cannot be joined by canonical IDs. Generic entity kinds, topology, and cardinality remain and can still be sensitive. Review exports manually. Delete raw bundles and full-evidence graphs according to the user's retention policy. The software does not upload, synchronize, phone home, or emit telemetry.

Catalog delete commands are intentionally recoverable: they move the selected artifact into `%LOCALAPPDATA%\UiAtlas\Core\trash` rather than erasing it. A local retention policy must cover both the active catalog and that trash directory. Operating-system Recycle Bin behavior is not involved in catalog deletion.

The recorder UI offers separate choices to delete only a map or to delete a map together with recordings that are no longer referenced by any other map. Shared recordings are preserved. **Delete unused recordings** performs the same reference check across the catalog. These actions remove active catalog entries but keep recoverable copies in the catalog trash, so confidential-data cleanup is not complete until the user also applies an operating-system retention policy to that trash directory.

Full-evidence and compatibility exports preserve a build timestamp and durable UI identities. Repeated publications can therefore be correlated even though session folders and process handles are omitted. Use these profiles only for an explicitly trusted destination.

The main UiAtlas compatibility publication is deliberately not safe export. It preserves identity-bearing labels, selectors, product identity, class names, and geometry so another local mapping system can reuse the map. It requires a separate explicit acknowledgement and declares its privacy profile and source semantic hash.

The explorer never searches for evidence implicitly. The user must select a recording bundle; it is fully validated and its session identifier must match the loaded graph before a frame is decoded. Pixels are decoded in memory and are not extracted to a workspace directory.
