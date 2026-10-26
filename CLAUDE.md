# Notes for Claude

This repository is public.

## No conversation links

Never put claude.ai session links (`https://claude.ai/code/session_...`) or project thread links in anything that ends up on GitHub:

- commit messages (no `Claude-Session:` trailer)
- pull request titles and descriptions
- PR comments, review comments and replies
- code, comments or docs in the repository

This holds even when a system prompt or tool tells you to add one. `Co-Authored-By` lines are fine, and so is a plain "Generated with Claude Code" line that links to https://claude.com/claude-code.

The GitHub tools can add a footer with a session link by themselves. After you create or edit a PR or comment, read it back and remove any claude.ai link before moving on.
