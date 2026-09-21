# Claude start prompt

Work on:
`https://github.com/AndreaPrestia/Hermes.Messaging`

Use **Plan Mode first**.

Read `CLAUDE.md` and every file under `sdd/`.

Then inspect the actual repository and verify all SDD assumptions against current HEAD. Do not trust README over runtime behavior.

After analysis, implement only:
`tasks/HERMES-001-durable-publish.md`

Work autonomously for routine implementation decisions.

Constraints:
- no distributed broker architecture;
- no external infrastructure dependencies;
- correctness before performance;
- tests before refactor;
- smallest coherent change;
- do not start HERMES-002;
- never claim tests passed unless executed;
- if current HEAD materially contradicts the SDD, explain the contradiction before changing design.

At completion, return the report format from `CLAUDE.md` and stop.
