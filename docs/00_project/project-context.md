# Project context

ContractScribe is a policy-driven, evidence-grounded C# XML documentation audit and safe proposal system.

It distinguishes missing documentation from documentation worth writing, grounds future proposals in bounded repository evidence, and constrains future patches to XML documentation changes. It is not a general coding agent.

The product value is the ability to produce accurate, consistent XML documentation. Its trust boundary is a deterministic pipeline: Roslyn-based audit identifies targets and evidence, the Documentation Scribe returns structured proposals, a deterministic patch engine applies only XML-documentation changes, and a platform adapter owns GitHub side effects.

M0 is complete. The repository now contains provisional policy, taxonomy, and audit-result contracts; synthetic conformance fixtures; framework-dependent Roslyn/MSBuild experiments; an independently validated execution baseline; and ADRs selecting a framework-dependent, in-process M1 topology. Production audit loading, policy evaluation, proposal generation, safe source modification, campaign state, GitHub write operations, and a consumable GitHub Action are not yet implemented.

M0's durable product asset is a [semantic foundation](../20_architecture/semantic-foundation.md): Policy expresses normative expectations, Symbol and Evidence Taxonomy describes targets and bounded facts, and Audit Result produces deterministic judgments. Later stages extend this versioned contract chain with work-plan, context, style, proposal, patch, state, and publication contracts rather than redefining the M0 language.

The Documentation Scribe is a project-specific, bounded agent role rather than a general coding-agent dependency. Its Scribe Runtime may read only allowlisted repository evidence through bounded tools and may submit only a structured documentation proposal. It does not receive a shell, arbitrary file editing, GitHub mutation, or authority to bypass deterministic validation.

The planned initial model transport is limited to an OpenAI-compatible protocol. DeepSeek is the primary development and economic-evaluation target, while MiMo is the compatibility-evaluation target; support claims wait for the M3 executable provider corpus and evidence. Repository context is bootstrapped deterministically and completed through the same Scribe's bounded read-only tools. Multiple short-lived Scribe runs reuse immutable repository and scope context snapshots; they do not share mutable conversation history or use parent/child agents. Stable prompt-prefix reuse and bounded uncached input are required economic properties, while provider cache availability is never a correctness dependency.

The first intended consumer experience is a GitHub workflow that can run on a caller-selected schedule or manual trigger and open reviewable documentation-only pull requests. Distribution, licensing, and public release remain separate gates from source-based feature development.
