# ADR 0006: Explicit browser-storage recovery instead of migrations

- Status: Accepted
- Date: 2026-07-12

## Context

The application stores a complete SQLite WASM database as a versioned Base64 value in `localStorage`. Earlier code ignored a schema mismatch and created/persisted fresh seed data, which could silently overwrite user changes. It also copied the main database before the active connection was closed and without checkpointing WAL, causing reproducible save-then-reload data loss.

Full migrations would require retaining and testing every historical browser database schema, transactional backup/rollback inside the WASM file system and a support policy beyond this portfolio demo's scope.

## Decision

Choose explicit demo-storage behavior:

- schema version 3 represents the current model;
- incompatible, corrupt, truncated or unreadable payloads enter typed recovery state;
- the stored payload is not overwritten automatically;
- users can export the original versioned payload and explicitly confirm reset/reseed;
- snapshots checkpoint WAL, dispose the context, then read the main database file;
- restored databases pass header, `PRAGMA quick_check` and expected-schema checks;
- quota/write failure is reported as persistence failure, not success.

## Consequences

Data survives ordinary reloads and failures no longer masquerade as migrations. Unsupported historical data requires export/reset; there is no promise that the exported JSON can be re-imported by a future version. A production product would need versioned migrations, backup/rollback, storage encryption decisions and support ownership.
