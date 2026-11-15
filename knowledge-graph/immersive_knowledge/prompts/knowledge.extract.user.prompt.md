---
name: knowledge.extract.user
version: 2
purpose: User message of the extraction request: the causal snapshot plus the one chapter (or part of a chapter) to process.
response: json
variables:
  - volume | type=int | required
  - chapter | type=int | required
  - chapter_title | default=
  - part_label | default=
  - snapshot_json | required | untrusted
  - chapter_text | required | untrusted
  - retry_note | default=
---
VOLUME {{volume}} CHAPTER {{chapter}} {{chapter_title}} {{part_label}}

SNAPSHOT (everything already known from earlier text; use these ids):
{{snapshot_json}}

CHAPTER (process only this text; quotes must come from it):
{{chapter_text}}

{{#if retry_note}}
YOUR PREVIOUS ANSWER WAS REJECTED: {{retry_note}}

{{/if}}Return the JSON object now.
