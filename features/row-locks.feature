# Executable acceptance specification (Reqnroll): row locks, taken by a read
# inside a transaction. Written before the implementation.
@implemented
Feature: A transaction can lock the rows it reads
  As an F# application developer whose workers claim jobs from one table
  I want a read that locks the rows it returns until the transaction ends
  So that two workers never claim the same job, and a change made from a read
  is not lost to another writer

  A query locks its rows with `locking`, as the last clause of a plasm { }
  query: RowLock.ForUpdate, ForNoKeyUpdate, ForShare or ForKeyShare, each
  waiting for a row another transaction holds, or `.NoWait` (fail at once) or
  `.SkipLocked` (leave the row out). A locked query is a Locked<'T>, which only
  Tx.all and Tx.one read: a lock outside a transaction cannot be written. The
  lock is on the query's own rows (FOR UPDATE OF its table); what they own or
  link, loaded with them, is read and not locked. Only a query of records is
  locked, so a projection (`select`, `selectDistinct`) has no lock to take,
  and a grouped query, which must end in `select`, is refused when it is
  checked; a provider without row locks refuses a locked read by name. A NOWAIT read of
  a row another transaction holds fails as DbFailureKind.LockNotAvailable.

  # Job = { Id: JobId (generated); Kind: string; Queued: bool; Notes: JobNote list (owned) }
  Background:
    Given a configured relational database
    And jobs with their notes

  Rule: Two transactions see each other's locks

    Scenario: Two workers claiming with SKIP LOCKED each get a different job
      Given three queued jobs
      When two workers each claim the first queued job, skipping locked ones, the first holding its lock until the second has claimed
      Then the workers claimed different jobs

    Scenario: A claim with NOWAIT on a job another worker holds fails at once
      Given three queued jobs
      When a worker holds the first queued job and a second claims it without waiting
      Then the second's transaction fails as a lock that is not available

    Scenario: A shared lock is shared, and keeps out a lock for update
      Given three queued jobs
      When a worker holds the first queued job with a shared lock, a second takes a shared lock on it without waiting, and a third locks it for update without waiting
      Then the first two read the job
      And the third's transaction fails as a lock that is not available

  Rule: The lock is part of the query, and only a transaction reads it

    Scenario: A locked read sends its lock on the query's own rows, not on what they own
      Given three queued jobs
      When a transaction reads the first queued job locked for update, with its notes
      Then the job's statement ends with FOR UPDATE OF "j"
      And the statement that reads its notes takes no lock

    Scenario: A locked query read outside a transaction does not compile
      When I compile code that reads a locked query with repo.All
      Then the locked read does not compile

    Scenario: A lock on grouped rows is refused when the query is checked
      When I check the locked queries that lock grouped rows
      Then the problem is reported for "lockedGroups", saying "a grouped query returns summaries, not records"

    Scenario: A provider without row locks refuses a locked read
      Given three queued jobs
      When a transaction reads the first queued job locked for update, through a provider without row locks
      Then the read is refused as unsupported by that provider

    Scenario: A provider whose writers take turns deletes what a record owns without locking a row
      Given three queued jobs
      When the first job is deleted through a provider without row locks, whose writers take turns
      Then the first job and its notes are deleted
      And no statement sent locks a row
