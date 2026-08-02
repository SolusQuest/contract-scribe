# M1 production Host calibration

Status: Issue #24 implementation evidence; exact-revision cross-platform certification remains owned by Issue #26.

Date: 2026-08-02

## Purpose and authority

This record selects the concrete cooperative timeout, diagnostic, temporary-disk, and process-observation bounds owned by Issue #24. The machine-readable authority is `src/ContractScribe.Core/Hosting/host-calibrated-bounds-v1.json`. Every bound entry references the SHA-256 of `host-calibration-evidence-v1.json`, and Core rejects a manifest whose evidence reference does not match the embedded evidence bytes.

This is implementation calibration, not Host Validation certification. The exact-revision Ubuntu X64 and Windows X64 execution matrix, independent observations, and evidence publication remain owned by Issue #26.

## Method

The representative implementation sample ran the real in-process Host composition on Windows X64. The recorded TRX durations include fixture creation, local package preparation, Host execution, and fixture cleanup, so they are conservative end-to-end context rather than isolated stage latency. Deterministic control seams separately forced SDK discovery, workspace loading, and shutdown implementations to ignore cancellation while a 50 ms test deadline raced them. Each test required the Host to return a normalized timeout promptly and to observe the late task without changing the committed terminal cause.

The representative full-composition sample completed in 4,336 ms. The controlled SDK, workspace-load, and shutdown cases completed end to end in 2,937 ms, 3,255 ms, and 8,345 ms respectively. A second real-composition resource observation measured 8,236 bytes across the governed temporary roots and three descendant process identities. Test duration is not used as a promise of future performance; it establishes a current implementation sample from which deliberately conservative pre-release limits are selected.

## Selected bounds

| Bound | Class | Limit | Calibration basis |
| --- | --- | ---: | --- |
| `sdk-discovery-timeout` | internally enforceable | 20,000 ms | More than four times the representative full run; the 50 ms blocking-provider stimulus proves the wait itself is cancellable. |
| `workspace-load-timeout` | internally enforceable | 120,000 ms | More than 27 times the representative full run; the 50 ms blocking-loader stimulus covers the complete load task, including tail inventory. |
| `total-audit-timeout` | internally enforceable | 300,000 ms | More than 69 times the representative full run and covers every managed stage after accepted input. |
| `graceful-shutdown-timeout` | internally enforceable | 10,000 ms | More than twice the representative full run; the 50 ms synchronous-entry stimulus proves shutdown cannot indefinitely block the caller. |
| `diagnostic-count` | internally enforceable | 32 facts | Forty distinct facts truncate deterministically to 32 after ordering and deduplication. |
| `diagnostic-utf8-bytes` | internally enforceable | 32,768 bytes | Maximum-sized public-safe fact arguments truncate before the canonical envelope exceeds 32 KiB. |
| `temporary-disk-bytes` | internally enforceable | 16,777,216 bytes | More than 2,000 times the 8,236-byte representative high-water; transient 4,096-byte files are observed and limit plus one byte prevents commit. |
| `toolchain-subprocess-count` | observable only | 32 identities | More than ten times the representative count of three; stable PID/start identity, ancestry, grandchildren, and PID reuse are tested. |

## Enforcement boundary

SDK discovery, workspace loading, the total managed audit, graceful shutdown, diagnostics, and governed temporary disk are internally bounded. The selected C1 topology cannot forcibly terminate non-cooperative in-process code. Pre-managed-entry launch/runtime/permission limits and fatal process termination remain caller- or OS-enforced and produce no fabricated Host terminal record. Descendant process count is an observation, not a termination guarantee. Atomic rename does not claim power-loss durability.

The bounds are intentionally conservative for pre-release operation. A later implementation sample exceeding a limit is not silently accommodated: it requires an evidence update, a new evidence digest, the corresponding calibrated-bounds digest, ordinary review, and whatever exact-revision Host Validation work is then applicable.

## Reproduction

Run the focused `ProductionHostContractTests` and `ProductionAuditHostTests` suites. The named tests in `host-calibration-evidence-v1.json` cover the representative composition, non-cooperative stage boundaries, production diagnostic caps, transient disk high-water, and stable process identity. Run the repository's full formatting, build, test, structural bundle, dry-run, and self-test checks before publishing an Issue #24 revision.
