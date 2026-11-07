# Installation profiles

The base Python package supports reader enrichment, HTML, card JSON, dictionaries, caching and the subprocess API without print libraries. Install `immersive-reading[print]` for PDF generation/verification. Both profiles require Python 3.12 or newer and explicitly selected local dictionary resources. The full Windows portable ZIP includes the print profile.

For checkout development use `uv sync --frozen` for the base profile, or `uv sync --frozen --extra print --group release` for full release work. The lockfile is checked in; release tooling rejects installed versions outside the selected frozen dependency closure. Build tooling is a separate pinned dependency group and is not vendored into the portable runtime.

PDF requests from a base installation produce an actionable message identifying the missing print extra; independently completed reader data remains usable. Reader and HTML/card checks do not load font or PDF modules. Clean base and full environments are tested separately; optional extras are not merely simulated by blocking imports.
