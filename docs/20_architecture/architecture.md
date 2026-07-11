# Architecture

The intended architecture separates deterministic audit from later proposal and platform-adapter concerns:

```text
policy + repository evidence
  -> deterministic audit
  -> versioned audit result
  -> optional proposal protocol
  -> optional platform adapter
```

The deterministic audit has no network dependency, model/provider secret, or GitHub write token. A future adapter owns GitHub side effects; the core does not.

Bootstrap contains `ContractScribe.Core` and `ContractScribe.Cli` only. It intentionally does not create a Roslyn project or decide whether loading and semantic analysis share an assembly or process.
