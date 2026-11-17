# Executable acceptance specification (Reqnroll): unions with fields stored
# across columns. Written before the query translation, following core.feature.
@implemented
Feature: A union with fields is stored across columns
  As an F# application developer
  I want a field whose type is a union with fields stored as it is, without a second record
  So that my domain record is the stored record

  A union with fields is stored by convention: its case's name, in snake case,
  in the field's own column, and each case field in a column named
  <field column>_<case field> (a nested union likewise, from its own column).
  Cases with a field of the same name share its column. Every other column of
  the union holds NULL, so a row is always one case.

  # Card = { Id: CardId; Title: string; Stage: Stage }
  # Stage = Backlog | Doing of assignee: Person * since: DateTimeOffset
  #       | Done of resolution: Resolution * at: DateTimeOffset
  # Resolution = Shipped | Dropped of reason: string
  # cards(id, title, stage, stage_assignee, stage_since, stage_resolution,
  #       stage_resolution_reason, stage_at)
  Background:
    Given a configured relational database
    And a cards table with the Stage columns

  Rule: Each case is stored in its columns

    Scenario Outline: A Card <stage> is written and read back
      When I insert a Card <stage>
      Then reading it back gives the same Card
      And the cards table holds <columns>

      Examples:
        | stage                         | columns                                                                         |
        | in the backlog                | stage backlog, NULL in every other Stage column                                 |
        | that Ada is doing             | stage doing, stage_assignee ada and stage_since, NULL in every other Stage column |
        | done and shipped              | stage done, stage_resolution shipped and stage_at, NULL in every other Stage column |
        | done and dropped as "dup"     | stage done, stage_resolution dropped, stage_resolution_reason dup and stage_at, NULL in every other Stage column |

    Scenario: Changing the case clears the old case's columns
      Given a Card that Ada is doing
      When I change the Card to done and shipped
      Then stage_assignee and stage_since hold NULL

    Scenario: A row that is no case is refused when read, naming the field
      Given a cards row whose stage is doing without an assignee
      When I read the Cards
      Then the read fails for the Stage field, naming stage_assignee

    Scenario: A row with a value in another case's column is refused when read
      Given a cards row whose stage is backlog with an assignee
      When I read the Cards
      Then the read fails for the Stage field, naming stage_assignee

    Scenario: A union whose case holds a list cannot be stored in columns
      When I build a model of a record whose union has a case holding a list
      Then the model is refused for that field, saying a case field must be a scalar or a union

  Rule: Queries read a union as F# does

    Scenario: A case test
      Given a Card in each stage
      When I keep the Cards whose Stage.IsDoing
      Then only the Card that Ada is doing is returned

    Scenario: Comparing with a case without fields
      Given a Card in each stage
      When I keep the Cards whose Stage is Backlog
      Then only the Card in the backlog is returned

    Scenario: Matching on a case's field
      Given a Card in each stage
      When I keep the Cards that are Doing by Ada, written as a match
      Then only the Card that Ada is doing is returned

    Scenario: Matching on a nested case's field
      Given a Card in each stage
      When I keep the Cards that are Done and Dropped as "dup", written as a match
      Then only the Card done and dropped as "dup" is returned

    Scenario: The domain's own member reads as its column
      Given a Card in each stage
      When I keep the Cards whose Assignee is Some Ada, through the member Card.Assignee
      Then only the Card that Ada is doing is returned
      And the SQL compares the column stage_assignee, without a CASE

    Scenario: Selecting case fields through the domain's members reads their types
      Given a Card in each stage
      When I select the title, Card.Assignee and Card.Since of the Cards that Ada is doing
      Then the row holds the title, Some Ada and Some of the time she started

    Scenario: Comparing with a whole value
      Given a Card in each stage
      When I keep the Cards whose Stage equals the value Doing (Ada, since)
      Then only the Card that Ada is doing is returned

    Scenario: Counting by case, through the domain's own member
      Given a Card in each stage
      When I count the Cards grouped by the member Card.Phase, which names each case
      Then the counts are 1 Waiting, 1 Working and 2 Finished

    Scenario: Counting by a member that names each case in words
      Given a Card in each stage
      When I count the Cards grouped by the member Card.PhaseName, which names each case in words
      Then the counts are 1 "not started", 1 "in progress" and 2 "it's done"

    Scenario: A stage no case names is refused through a member, as reading the Card is
      Given a cards row whose stage is archived, which no case names
      When I read the Cards' phase names through the member Card.PhaseName
      Then the read fails, naming the match on the Card's Stage, rather than giving a phase

    Scenario: Ordering the cards of one case by that case's field needs no NULLS clause
      When I explain the Cards in progress, the latest started first
      Then the SQL orders by stage_since DESC, with no NULLS clause

  Rule: Bulk writes and the schema check know the columns

    Scenario: A bulk update sets every column of the union
      Given a Card in each stage
      When I bulk update every Card back to Backlog
      Then every Card reads back in the backlog
      And every other Stage column holds NULL

    Scenario: A missing case column is reported
      Given the cards table has lost its stage_since column
      When I check the cards model against the database
      Then the missing column stage_since is reported for Card.Stage
