Feature: A background job sends stale cards back to the backlog
  A card in progress for longer than the domain allows (Card.staleAfter, 14
  days) is sent back to the backlog by a job the host runs when it starts and
  then at an interval. The job is a C# BackgroundService, which says when;
  "now" is the host's one clock (clock.feature), and which cards are stale
  and what sending one back does are the F# core's (Workflows.fs,
  sendStaleBack): one UPDATE over every stale card, each card's version
  incremented as by any change (docs/kanban-host.md).

  Background:
    Given the host serves a migrated database
    And the host sends stale cards back every second
    And board "apollo" has cards in progress:
      | title    | assignee | started     |
      | Guidance | Ada      | 15 days ago |
      | Docking  | Grace    | 20 days ago |
      | Re-entry | Ada      | 1 day ago   |

  Rule: Cards in progress longer than the domain allows go back to the backlog

    Scenario: The job sends the stale cards back when the host starts
      When the host has sent stale cards back
      Then the host sent 2 cards back
      And "Guidance" is in the backlog
      And "Docking" is in the backlog
      And "Re-entry" is in progress for "Ada"

    Scenario: A card sent back by the job is a new version
      When the host has sent stale cards back
      Then "Guidance" is at version 2
      And "Re-entry" is at version 1

    Scenario: A copy read before the job ran is stale
      Given "Margaret" is signed in with the password "moonshot-1969"
      When the host has sent stale cards back
      And "Guidance" is renamed "Navigation" from version 1
      Then the answer is 409

    Scenario: The job runs again at its interval
      When the host has sent stale cards back
      And "Re-entry" has been in progress for 15 days
      And the host has sent stale cards back again
      Then the host sent 1 card back
      And "Re-entry" is in the backlog
