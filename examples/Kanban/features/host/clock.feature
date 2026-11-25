Feature: The host has one clock
  "Now" is the TimeProvider the host registers in its services, and the host
  opens Boards with the same one: every change is stamped with its time,
  and the background job reads its "now" from it too (docs/kanban-host.md).
  A host started with KANBAN_CLOCK_START has a clock that starts at that
  instant and runs from there.

  Background:
    Given the host serves a migrated database

  Scenario: A change is stamped with the host's clock
    Given the host's clock starts at "2030-01-01T09:00:00Z"
    And "Margaret" is signed in with the password "moonshot-1969"
    And the board "apollo" allows 2 cards in progress
    And the cards "Guidance" on "apollo"
    When the person signed in starts "Guidance"
    Then "Guidance" was started at "2030-01-01T09:00:00Z"

  Scenario: The job and the changes read the same clock
    Given the host's clock starts 30 days from now
    And the host sends stale cards back every second
    And "Margaret" is signed in with the password "moonshot-1969"
    And the board "apollo" allows 2 cards in progress
    And the cards "Guidance" on "apollo"
    And "Guidance" is in progress
    When the host has sent stale cards back again
    Then "Guidance" is in progress for "Margaret"
