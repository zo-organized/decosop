# DecoSOP architecture

These documents describe how DecoSOP is built, for developers working on the codebase. The
user-facing setup and operating guide is the [top-level README](../../README.md).

| Document | Covers |
|---|---|
| [System architecture](system-architecture.md) | What runs where: the host machine, the watched folders, the sync tooling, browsers on the LAN, the files DecoSOP keeps on disk, and how releases are built, installed and updated. |
| [Code architecture](code-architecture.md) | How the code is organised: startup and DI, the data model, the folder-sync engine, the search pipeline, the Blazor UI, concurrency rules, and the invariants to preserve when changing things. |

## DecoSOP in one paragraph

DecoSOP is a single-process ASP.NET Core / Blazor Server app (.NET 10) that runs as a Windows
Service. It mirrors up to two folders on disk (**SOPs** and **Documents**) into a SQLite catalog,
keeps that catalog in sync with a file watcher plus a periodic rescan, builds a separate SQLite
FTS5 full-text index of document contents in the background, and serves a browsable,
searchable UI to browsers on the local network. **The folder is the source of truth**:
everything in the databases can be rebuilt from it, except per-machine favorites, pins and
folder colours.
