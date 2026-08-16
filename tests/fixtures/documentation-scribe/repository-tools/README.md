# Repository text tool selection

Issue #105 selects three product-owned, read-only operations:

- `repository.read-excerpt` reads a complete bounded UTF-8 file or an exact line range.
- `repository.list-files` discovers deterministic repository-relative regular-file identities beneath a caller-frozen directory scope.
- `repository.search-text` finds ordinal, case-sensitive, single-line literal matches and returns bounded surrounding-line excerpts.

All three are needed for the accepted Documentation Scribe path: an instruction or evidence anchor can identify an authority scope, listing or literal search can discover a maintained artifact that Roslyn does not model, and excerpt reading can retrieve the selected evidence. Regex, glob expansion, fuzzy search, Git, shell/process execution, web access, writes, arbitrary encodings, general filesystem handles, and durable indexes or cursors are intentionally absent.

Scopes are created by trusted composition from IDs already visible in the exact first model request. Provider arguments can narrow a frozen scope but cannot choose authority, role, subject, claims, route origin, budgets, or mutation capability. Every no-cursor paging call creates an independent bounded run-local chain, so identical calls in one tool round and calls after provider retry obey one lifecycle-independent rule without invalidating an earlier published cursor.

The implementation independently bounds calls, enumerated entries and files, directory depth and aggregate directory visits, complete bytes read (including final freshness reads), returned content and items, literal matches, route depth, active cursor chains, page size, and elapsed time. Safety, staleness, cursor, cancellation, and hard-budget failures publish no content, evidence, or cursor.

`text/unicode-newlines.txt` and `authority/promotion.md` are stable positive fixtures. Tests create filesystem-specific negative objects (invalid UTF-8, NUL/binary content, huge files and lines, case collisions, hard-link aliases, symlink/reparse escapes, and replacements) inside isolated temporary repositories because several of those objects cannot be represented portably by Git.
