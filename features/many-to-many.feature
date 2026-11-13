# Executable acceptance specification (Reqnroll): many-to-many relationships.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Many-to-many relationships
  As an F# application developer
  I want records linked through a join table to load and change like other relationships
  So that tags, roles and categories need no hand-written join-table code

  A many-to-many relationship is declared with its join table. Loading it is
  batched like every other relationship; changing it writes only join-table
  rows and never the linked records.

  Background:
    Given a configured relational database
    And mappings for Articles and Tags linked through a join table

  Rule: Many-to-many relationships load like other relationships

    Scenario: Preloading a many-to-many relationship
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I query Articles and preload their Tags
      Then each Article has exactly its linked Tags
      And the Tags are loaded with one batched command through the join table
      And a Tag linked to several Articles is materialised once

    Scenario: An Article without Tags has an empty, loaded relationship
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I query Articles and preload their Tags
      Then an Article without links has Tags loaded as an empty list

    Scenario: Loaded Tags can be filtered, ordered and limited per Article
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I preload Tags filtered by name, ordered by name and limited to 1 per Article
      Then each Article has at most 1 Tag satisfying the filter
      And the filter, ordering and limit run in the database

    Scenario: Filtering through a many-to-many relationship
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I keep only Articles that have a Tag with a given name
      Then only those Articles are returned, each once
      And the condition runs in the database through the join table

  Rule: Changing links writes only the join table

    Scenario: Replacing an Article's Tags
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I replace an Article's Tags with a different set
      Then the join table links the Article to exactly that set
      And the returned Article has the new Tags loaded
      And the Tags themselves are not written
      And other Articles keep their links

    Scenario: Adding and removing individual links
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I add one link and remove another
      Then only those links change
      And the returned Article's Tags are not loaded, because the full set was not read

    Scenario: Inserting an Article with its Tags
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I insert a new Article linked to existing Tags
      Then the Article and its links are written in one transaction
      And the returned Article has its generated key and its Tags loaded

    Scenario: Linking to a record that does not exist changes nothing
      Given Articles linked to Tags, with one Tag shared by several Articles
      When I change an Article's title and link it to a Tag that does not exist
      Then the write fails with a classified foreign key violation
      And neither the title nor the links were changed

    Scenario: Link writes need a many-to-many relationship
      When I attempt to write links through a has-many relationship
      Then the changeset is rejected before execution with the relationship identified
