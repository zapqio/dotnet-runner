# Project instructions

## Code readability

- Prioritize readable code when writing or refactoring.
- Always use braces for `if`, `else if`, and `else` blocks, even when the body contains only one statement.
- Insert a blank line after the complete conditional block before the next statement. Keep related `else if` and `else` branches together; do not insert a blank line before a closing brace solely to satisfy this rule.

## Browser automation artifacts

- Never create .playwright-mcp directories or browser automation snapshots, screenshots, traces, or logs inside repositories. Use absolute output paths in the Windows temporary directory, such as %TEMP%\codex-playwright.
- Do not pass repository-relative filenames to browser tools. Before browser automation, ensure the active server writes its automatic output outside the repository.
- After changing MCP output settings, reload or restart the server before further browser actions; do not reuse a server that still has the previous repository output settings.

## Plans

- Keep implementation plans and AI analyses in the `ai_analysis/` directory, not in the repository root.

## Local secrets and security checks

- Use only local/test credentials. Keep values out of commands, tool arguments, transcripts, reports and tracked files. Read them from the process environment, .NET User Secrets, or ignored local files without printing them.
- In PowerShell, use `$env:NAME` for environment variables. Do not expand secrets into a child process command line; use its environment, standard input or protected configuration files.
- Run `scripts/security/Install-Gitleaks.ps1 -InstallHooks` when preparing a checkout. Never bypass hooks or disable scanner rules to make a commit pass. Missing tools and failed scans must stop the operation.
- Run `scripts/security/Scan-Secrets.ps1 -Mode WorkingTree` before proposing a commit. Investigate findings without displaying secret values. Test-only exceptions must be narrow and reviewed.
- Local analysis files belong in ignored `ai_analysis/`. These rules govern development and agent work; preserve the end-user installer token input interface and documented token placeholders.
