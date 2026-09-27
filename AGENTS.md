# Repository Guidelines

## Project Structure & Module Organization

This repository implements an AI selection toolbar for Windows and Linux.

- `src/Core/` contains shared C# models, API streaming, settings, history, and notes.
- `src/Desktop/` contains the Windows WPF application and the local management page.
- `src/Selection/` contains Windows text-selection and trigger logic.
- `src/Linux/` contains the Python/PySide6 X11 client and its C# host project.
- `tests/` contains Linux `unittest` modules and Windows package/stream smoke tests.
- `packaging/linux/`, `installer/`, and `.github/workflows/` contain packaging scripts and CI.
- `docs/` contains platform, development, release, and acceptance notes.

Keep platform-specific behavior in its platform directory; place reusable Windows/Linux C# contracts in `src/Core/`.

## Build, Test, and Development Commands

Linux development uses Python 3.10+ and an X11 session:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -e '.[build]'
python -m unittest tests/test_linux_settings.py tests/test_linux_position.py tests/test_linux_history.py
```

Run the application with `ai-selection-toolbar`. Build the portable Linux executable with the PyInstaller command in `.github/workflows/linux-build.yml`; package it with `packaging/linux/make-installer.sh`.

On Windows, restore and build with:

```powershell
msbuild src\Desktop\AiSelectionToolbar.Desktop.csproj /t:Restore,Build /p:Configuration=Release /p:Platform=AnyCPU
```

Use `installer\AiSelectionToolbar.iss` for the installer and run `tests\windows-package-smoke.ps1` for package smoke checks.

## Coding Style & Naming Conventions

Use four spaces in Python and standard C# formatting consistent with nearby files. Use `PascalCase` for C# types and public members, `camelCase` for local C# variables, and `snake_case` for Python functions and variables. Keep HTML, CSS, and JavaScript changes localized to the management page. Avoid unrelated formatting or renaming.

## Testing Guidelines

Name Python tests `test_*.py` and keep them runnable with `python -m unittest`. Set `QT_QPA_PLATFORM=offscreen` for headless Linux tests. There is no repository-wide coverage threshold; add focused regression tests for behavior changes. Desktop/X11, target-application selection, and packaging behavior also require platform-appropriate smoke or manual validation.

## Commit & Pull Request Guidelines

Use Conventional Commits with a concise scope, for example `fix(linux): improve selection toolbar layout` or `feat(v0.5): improve history UI`. Pull requests should explain the user-visible change, identify platform impact, link relevant issues or requirements, and include test commands and results. Include screenshots or recordings for UI changes and call out anything not verified on real Windows or X11 systems.

## Security & Configuration Tips

Never commit API keys, tokens, local configuration, `.venv`, or build output. Remote API endpoints must use HTTPS; HTTP is only acceptable for loopback services. Keep the local management server bound to `127.0.0.1` and review packaging contents before publishing artifacts.
