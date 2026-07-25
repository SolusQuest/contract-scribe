# Conventions

- Use English for public repository documentation, issue titles, and code.
- Keep repository surfaces free of private repository content, machine-local paths, prompts, raw provider responses, complete transcripts, raw logs, secrets, credentials, and private issue references.
- Use `main` as the default branch. After the one-time seed, all substantive changes use a branch and pull request.
- Use conventional PR titles: `<type>(<scope>): <summary>`.
- Keep published fixtures synthetic and free of secrets, private source, private identifiers, and machine-local paths.
- Do not describe the repository as open source or invite contributions until a license decision is recorded.
- Use **XML documentation comment**, **documentation block**, and **documentation target** for C# `///` documentation. Avoid **XML header**, which can be confused with file headers.
- Treat pre-release artifact versions as compatibility-family identifiers, not change counters. Pin draft semantics to a commit and follow [Contract lifecycle](contract-lifecycle.md).
- Do not describe model-generated text as applied or safe until the deterministic patch engine has rendered and validated it.
- Add C# projects according to [Project structure](../20_architecture/project-structure.md). Do not create a project merely because a milestone exists, and do not treat fixture or experiment projects as product assemblies.
- Treat a future TypeScript Action package as a host artifact, not as a second implementation of provider, campaign, ledger, patch, or GitHub-publication behavior.
