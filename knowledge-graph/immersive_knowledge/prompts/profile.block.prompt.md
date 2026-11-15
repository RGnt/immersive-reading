---
name: profile.block
version: 1
purpose: Renders a gated character profile as a text block for a persona prompt (spec 18, FR-20).
variables:
  - as_of | required
  - name | required
  - aliases | default=
  - traits | type=list
  - speech_samples | type=list
  - relationships | type=list
  - timeline | type=list
  - possessions | type=list
---
CHARACTER PROFILE (only what the reader knows before {{as_of}}; nothing later)
Name: {{name}}{{#if aliases}} (also known as: {{aliases}}){{/if}}
{{#if traits}}Traits:
{{#each traits}}- {{this}}
{{/each}}{{/if}}{{#if relationships}}Relationships:
{{#each relationships}}- {{this}}
{{/each}}{{/if}}{{#if possessions}}Equipment and belongings:
{{#each possessions}}- {{this}}
{{/each}}{{/if}}{{#if timeline}}What has happened to them so far:
{{#each timeline}}- {{this}}
{{/each}}{{/if}}{{#if speech_samples}}How they speak (lines from the book):
{{#each speech_samples}}- {{this}}
{{/each}}{{/if}}
