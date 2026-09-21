# Contributing to OpenLume

OpenLume welcomes focused issues and pull requests.

1. Check the roadmap and existing issues before starting work.
2. Keep a change to one user-visible behavior or failure class.
3. Add tests for catalog, imaging, or provider behavior.
4. Run `dotnet build OpenLume.slnx --configuration Release` and `dotnet test OpenLume.slnx --configuration Release`.
5. Do not add model weights, proprietary Adobe profiles, private photographs, API keys, or generated catalogs.

All imaging operations must preserve originals. Cloud features must be disabled by default, visibly identify when an upload will occur, and store credentials in the operating-system credential store.

Contributions are accepted under GPL-3.0-or-later. New dependencies must have a compatible license recorded in `THIRD_PARTY_NOTICES.md`.

