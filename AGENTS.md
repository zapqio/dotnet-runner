# Project instructions

## Code readability

- Prioritize readable code when writing or refactoring.
- Always use braces for `if`, `else if`, and `else` blocks, even when the body contains only one statement.
- Insert a blank line after the complete conditional block before the next statement. Keep related `else if` and `else` branches together; do not insert a blank line before a closing brace solely to satisfy this rule.

## Browser automation artifacts

- Never create .playwright-mcp directories or browser automation snapshots, screenshots, traces, or logs inside repositories. Use absolute output paths in the Windows temporary directory, such as %TEMP%\codex-playwright.
- Do not pass repository-relative filenames to browser tools. Before browser automation, ensure the active server writes its automatic output outside the repository.
- After changing MCP output settings, reload or restart the server before further browser actions; do not reuse a server that still has the previous repository output settings.
