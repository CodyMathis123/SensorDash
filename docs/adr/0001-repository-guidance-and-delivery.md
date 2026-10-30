# 0001: Shared repository guidance and validated PR delivery

- Status: Accepted
- Date: 2026-10-30

## Context
Development has continued across AI chats. Architectural context and the owner's established test/pipeline/merge policy must survive chat boundaries and be reviewable alongside source.

## Decision
Use a root `AGENTS.md` as the single contributor instruction entry point, linked project/source map and development guide, and numbered Markdown ADRs. Keep these files vendor-neutral and versioned with code. Use the existing Windows workflow as the full validation gate, automatically triggered on PRs targeting Production. After successful current-revision validation, merge without a separate confirmation, then verify the Production pipeline.

## Alternatives
Chat-only memory is difficult to audit and loses context. Tool-specific duplicated rule files drift. A dedicated agent orchestration framework adds dependencies and maintenance without a demonstrated product need. Generated exhaustive source inventories become stale faster than a responsibility map.

## Consequences and validation
Contributors have a small shared starting point and explicit completion criteria. Documentation must change with architecture. PR builds increase CI usage but expose Windows and installer failures before merge. Existing protections and privileged hardware approval remain in force. Validate the workflow on the PR that introduces it and confirm both its artifacts and the post-merge run.
