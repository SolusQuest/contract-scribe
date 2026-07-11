# Project context

ContractScribe is a policy-driven, evidence-grounded C# XML documentation audit and safe proposal system.

It distinguishes missing documentation from documentation worth writing, grounds future proposals in bounded repository evidence, and constrains future patches to XML documentation changes. It is not a general coding agent.

The current bootstrap provides only repository governance and a minimal CLI surface. It does not implement audit, Roslyn loading, policy evaluation, JSON contracts, proposal generation, GitHub write operations, or a provider integration.
