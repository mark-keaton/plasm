# The Starter's generated text, committed

What the Starter's PostgreSQL build writes, kept here so the site can quote it
and a change to it cannot pass unseen: the package check (`../run.sh`) builds
the Starter's API from the packed packages and fails when its output is not
these files.

- `Views.g.cs`, `SeamHttp.g.cs`: what the seam writes into
  `examples/Starter/src/Starter.Api/Seam/` (which git leaves out).
- `ddl.postgres.sql`: what `Starter.Api ddl` prints.

A change to the seam's output or to the Starter's model changes them: copy
the new text over the old in the same commit.
