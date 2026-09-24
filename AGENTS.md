# Collaboration workflow

- The user wants completed milestones committed and pushed to the configured GitHub origin, after relevant validation. This is an ongoing preference; do not ask again for routine milestone commits and pushes.
- Keep commits focused, preserve unrelated user changes, and do not force-push or rewrite published history.
- The Plastic client is built from `src/TortoiseSCM.sln`; the retained upstream `src/TortoiseGit.sln` is still the Git client.
- GUI changes should follow the native TortoiseGit/TortoiseSVN dialog conventions. Use `src/Resources/TortoiseProcENG.rc` and the corresponding `src/TortoiseProc` dialogs as references, and verify normal and minimum-size rendering.
- Test repository server writes belong on dedicated `tortoisescm-autotest-*` branches and isolated workspaces. Preserve the original `TestSCM` workspace selector and existing files.
