# ContractScribe agent context

Read [Agent context](docs/50_ai/agent-context.md) for this repository's requirements, task routing, and validation commands.

Use Solus Book's [Collaboration](docs/shared/solus-book/standards/collaboration.md), [Source of truth](docs/shared/solus-book/standards/source-of-truth.md), [Conventions](docs/shared/solus-book/standards/conventions.md), and [Task routing](docs/shared/solus-book/agents/task-routing.md) for shared guidance. Project requirements and explicit exceptions remain in this repository.

[Book adoption](docs/50_ai/book-adoption.md) records the fixed source revision, initialization, skill entrypoints, and validation. Relative references inside Book resolve from their source files. Load only the rules and procedure relevant to the task.

The current task, working directory, applicable instruction hierarchy, and established authorization remain in force. Keep platform entrypoints thin and continue independent authorized work when a missing resource blocks another operation.
