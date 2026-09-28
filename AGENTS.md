# Collaboration workflow

- The user wants completed milestones committed and pushed to the configured GitHub origin, after relevant validation. This is an ongoing preference; do not ask again for routine milestone commits and pushes.
- Keep commits focused, preserve unrelated user changes, and do not force-push or rewrite published history.
- The Plastic client is built from `src/TortoiseSCM.sln`; the retained upstream `src/TortoiseGit.sln` is still the Git client.
- GUI changes should follow the native TortoiseGit/TortoiseSVN dialog conventions. Use `src/Resources/TortoiseProcENG.rc` and the corresponding `src/TortoiseProc` dialogs as references, and verify normal and minimum-size rendering.
- Test repository server writes belong on dedicated `tortoisescm-autotest-*` branches and isolated workspaces. Preserve the original `TestSCM` workspace selector and existing files.
- The product defaults to bundled native TortoiseGitMerge for diff/merge and includes TortoiseGitUDiff for unified diffs. Beyond Compare is a selectable alternative; preserve explicit user tool choices. Do not expand the retained in-process editors as a competing product direction. Beyond Compare three-way text merge requires Pro.
- User-defined completion scope excludes multi-DPI work, cross-restart recovery of failed-checkin drafts, and release/platform productization (formal signing, signed MSIX, automatic updates, ARM64/32-bit expansion, localization packages and enterprise deployment tooling). Do not restore these as required milestones. Keep existing Windows x64 packaging/install/uninstall maintenance, normal/minimum window checks, actual Explorer/Beyond Compare functional validation and the remaining SCM workflows in scope.
