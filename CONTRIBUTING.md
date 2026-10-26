# Contributing

1. Clone with `--recurse-submodules`.
2. Run `./scripts/build.sh` before opening a pull request.
3. Add a failing test before production behavior changes.
4. Do not add direct database writes or an independent file mover/copier.
5. Never log secrets or send absolute paths and unrelated filesystem data to an LLM.
6. Treat all LLM output as untrusted and verify IDs against authoritative candidates.
7. Keep import execution disabled unless the hardlink-only acceptance criteria in `docs/FEASIBILITY.md` are satisfied.
