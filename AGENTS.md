# Local AI developer rules

Project: DjShrek-Draiver Cyber Club, Windows 10/11 x64, C# / .NET 8 WPF.

## Working method
- Read README.md, docs/ARCHITECTURE.md and docs/ROADMAP.md before selecting work.
- Select one highest-priority unfinished roadmap item per run. Prefer a self-contained feature that can be completed safely.
- Implement real functionality; do not add placeholder buttons, fake progress, fabricated hardware values, or claims that a feature works when it does not.
- Preserve the established WPF visual identity, Russian UI wording, existing architecture and user-facing behavior.
- Keep changes focused. Do not rewrite unrelated files or reformat the whole project.
- Update ROADMAP.md only when the corresponding implementation is actually present. Do not mark work complete merely because a stub or interface exists.
- Add or update focused automated tests when practical. Do not claim hardware integration was tested on a real PC unless it was.
- If a task depends on undocumented vendor APIs, credentials, hardware, or unavailable data, document the blocker and choose another independently implementable task.

## Safety rules (mandatory)
- Never silently install, replace, downgrade, delete, or roll back a Windows driver.
- Never run pnputil installation/deletion commands, DISM driver removal, registry writes, firmware/BIOS tools, or destructive disk operations.
- Driver installation, backup restoration, registry cleanup and file deletion must remain behind explicit user confirmation and appropriate Windows elevation.
- Do not add telemetry, analytics, ads, credential collection, hidden network calls, or automatic downloads from untrusted hosts.
- Validate HTTPS host allow-lists, redirects, file size, hashes, INF hardware IDs, architecture, Windows build and catalog signature before presenting a driver candidate.
- Never invent driver versions, compatibility, signatures, sensor readings, scan progress or test results.
- Do not change licensing, ownership, price, payment details, or installer consent behavior without an explicit user request.

## Completion criteria
- The solution builds in Release on Windows with .NET 8.
- Existing validation and the app's --self-test pass where applicable.
- No secrets or local machine paths are committed.
- Summarize exactly what changed, what was built, what was not tested, and any remaining blocker.
