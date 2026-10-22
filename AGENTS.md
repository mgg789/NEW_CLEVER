# Project Notes For Codex

## Communication
- Respond to the user in Russian unless they explicitly ask for another language.
- The repository contains Russian/Ukrainian localization files. Keep touched text files valid UTF-8.

## Build And Run
- Main app project: `NEW_CLEVER/NEW_CLEVER.csproj`, classic WPF/.NET Framework 4.8.
- SDK projects: `Interpreter/Interpreter.csproj` and `InterpreterConsole/InterpreterConsole.csproj`.
- The current machine may not have the system .NET Framework 4.8 targeting pack installed. If `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8` is missing, use the local NuGet reference assemblies workflow below instead of assuming the normal solution build works.

### Clean Windows Build

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-new-clever.ps1
dotnet test NEW_CLEVER.AgentCore.Tests/NEW_CLEVER.AgentCore.Tests.csproj -c Release
```

- Packaging output: `NEW_CLEVER/bin/PackageRelease/NEW_CLEVER.exe`.
- Runtime-only assets and dependencies: `NEW_CLEVER/RuntimeData`; legacy generated `bin/obj` outputs are excluded from builds.
- CI workflow: `.github/workflows/new-clever.yml` on Windows.
- The app uses process-name single-instance detection and named pipe `NEW_CLEVER_pipe`.
- Compatibility data directories `%APPDATA%/CLEV3R`, `%LOCALAPPDATA%/CLEV3R` and `.clev3r/attachments` intentionally retain their historic names. Preserve these keys and existing user data.
- Resources/Settings designer files contain the renamed namespace/resource base. The current macOS environment has no ResGen or Visual Studio settings generator; regenerate them with Visual Studio if the resource/settings schema changes.

## Git Feature Notes
- Local Git functionality intentionally uses installed `git.exe`.
- Remote Git is implemented for HTTPS `origin` remotes through installed `git.exe`. Credentials are approved into Windows Git Credential Manager; do not put secrets into `.git/config` or remote URLs.
- Remote sync is intentionally conservative: `pull` requires saved tabs and a clean working tree, uses fast-forward-only updates, and reloads open project files on success. `push` sends all local branches without force and blocks non-fast-forward/diverged remote branches.
- Git status is based on saved files on disk. After `Ctrl+S`, Save All, Save As, or the save step before EV3 compile, the Git panel should refresh itself; Git actions also force a fresh `git status` before commit/message generation.
- Save-triggered Git refresh is intentionally debounced and status-only; full history refresh is reserved for init, reload, commit, restore, and branch switch.
- Git diff previews render parsed display lines in a read-only Scintilla tab. Keep Git metadata lines visually separate from added/deleted content instead of dumping raw patch text directly.
- Diff preview files are Scintilla fold headers and should open collapsed. Use hunk coordinates to insert compact hidden-context placeholders instead of expanding full unchanged file ranges.
- After local Git operations that change files on disk, call `MainWindowVM.ReloadOpenProjectFilesFromDisk(repositoryRoot)` so already-open `.bp/.bpi/.bpm` tabs update without reopening.
- The commit-history rollback button restores the repository files to the selected commit snapshot with `git restore --source <hash> --staged --worktree :/`, then leaves the rollback as local changes for the user to review and commit. It intentionally does not use `git revert`, because inverse patches can conflict after later edits to the same files.
- EV3 compilation adds a `.gitignore` block for `~*/` only when the compiled file is already under an initialized Git repository.
