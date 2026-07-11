# Pull request workflow

Create substantive changes on a branch and open a draft pull request unless the task explicitly requires ready-for-review status.

PR bodies must explain the change, link the tracking issue, record validation actually performed, and list remaining risks. A PR must not claim validation that did not run. The one-time repository bootstrap and an explicitly approved administrative change may state that no repository issue exists yet.

Before merging, review the diff for public-safety: no private downstream details, secrets, prompts, transcripts, logs, or unpinned private-only references may enter this repository.
