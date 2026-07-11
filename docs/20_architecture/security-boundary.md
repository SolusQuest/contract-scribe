# Security boundary

The deterministic audit baseline must not access a network, provider secret, or GitHub write token. Future proposal and publishing capabilities must be optional, explicitly configured, and separated from deterministic audit execution.

Public fixtures and CI must be synthetic and must not contain downstream-private source, prompts, transcripts, logs, credentials, or private paths. When a result cannot be supported by bounded evidence and a defined policy, the future system must produce a structured skip or fail closed rather than inventing a contract.
