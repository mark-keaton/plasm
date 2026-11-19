Feature: A test of the facade runs in a sandbox
  A test of what KanbanData does opens the facade once and runs each test in
  KanbanData.SandboxAsync: the facade it is given works on one connection in
  one transaction that is rolled back when the test ends, so every test sees
  the database as it was and leaves it so. C# sees the facade alone, never
  Plasm's repository (docs/guide/testing.md).

  Background:
    Given KanbanData is opened over a migrated database

  Scenario: What a test does in a sandbox is gone after it
    When a test in a sandbox creates board "alpha" and adds the card "Write" to it
    Then inside the sandbox, board "alpha" had the card "Write"
    And after the sandbox, there is no board "alpha"

  Scenario: The domain's refusal inside a sandbox leaves the sandbox usable
    When a test in a sandbox creates board "alpha" allowing 1 card in progress, starts one card and then a second
    Then inside the sandbox, the second start was refused with "WipLimitReached"
    And inside the sandbox, board "alpha" was read after the refusal with 1 card in progress
    And after the sandbox, there is no board "alpha"
