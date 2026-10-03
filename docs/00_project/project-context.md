# Project context

ContractScribe is a policy-driven, evidence-grounded C# XML documentation audit and safe proposal system.

It distinguishes missing documentation from documentation worth writing, grounds proposals in bounded repository evidence, and constrains patches to XML documentation changes. It is not a general coding agent.

The product value is the ability to produce accurate, consistent XML documentation. Its trust boundary is a deterministic pipeline: Roslyn-based audit identifies targets and evidence, the Documentation Scribe returns structured proposals, a deterministic patch engine applies only XML-documentation changes, and a platform adapter owns GitHub side effects.

M0 through M5 are complete: the production in-process audit Host and CLI, deterministic documentation patching, bounded Documentation Scribe, resumable campaign state, and GitHub proposal adapter are implemented. M6 delivered layered consumer configuration, a verified D2 DLL payload, a thin composite Action, caller workflow examples, release controls, and qualification of an exact unpublished internal candidate. The [M6 closeout](../90_roadmap/m6-plan.md) records the accepted engineering scope and evidence limitations; it is not a public release or an external support promise.

M0's durable product asset is a [semantic foundation](../20_architecture/semantic-foundation.md): Policy expresses normative expectations, Symbol and Evidence Taxonomy describes targets and bounded facts, and Audit Result produces deterministic judgments. Later stages extend this versioned contract chain with work-plan, context, style, proposal, patch, state, and publication contracts rather than redefining the M0 language.

The Documentation Scribe is a project-specific, bounded agent role rather than a general coding-agent dependency. Its Scribe Runtime may read only allowlisted repository evidence through bounded tools and may submit only a structured documentation proposal. It does not receive a shell, arbitrary file editing, GitHub mutation, or authority to bypass deterministic validation.

The implemented Scribe uses the transport and bounded evaluation accepted under M3. Additional providers and evaluation coverage remain separate work. Repository context selection is deterministic and completed through the same Scribe's bounded read-only tools. Runs do not share mutable conversation history or use parent/child agents, and provider cache availability is never a correctness dependency.

The first intended consumer experience is a GitHub workflow that can run on a caller-selected schedule or manual trigger and open reviewable documentation-only pull requests. Distribution, licensing, and public release remain separate gates from source-based feature development.

The accepted next milestone direction is M7: separate caller scheduling from product execution and provide automatic bounded continuation toward the remaining work. The current Action example requires explicit activation and defaults to recovery/replay of an existing publication; it does not yet provide that complete user experience. M7's detailed design, executable issues, and acceptance criteria are reserved for a separate refinement session. License/contribution disposition and first public publication are deferred until after M7, with their existing authority and release gates preserved.
