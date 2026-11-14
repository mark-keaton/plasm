# Executable acceptance specification (Reqnroll): linked collections, shared
# records a record links to. Written before the implementation.
@implemented
Feature: A linked collection is a plain list of shared records, loaded with its owner
  As an F# application developer
  I want my domain record to hold the shared records it links to as a plain list
  So that a record with a many-to-many relationship is still the stored record

  A schema states what its record links to through a join table:
  `links (fun c -> c.Tags) "card_tags" "card_id" "tag_id"`, or `linksOrderedBy`
  with an order. A linked list is always loaded with its owner. Writing the
  owner writes only rows of the join table: the linked records belong to their
  own aggregates, so they are never inserted, changed or deleted through it.

  # Card = { Id: CardId; Title: string; Tags: Tag list }, Tag = { Id: TagId; Name: string }
  # Tags are linked through card_tags(card_id, tag_id), ordered by name.
  Background:
    Given a configured relational database
    And tags "urgent", "docs" and "bug", and cards, linked through card_tags

  Rule: A linked list is always loaded

    Scenario: Reading a card reads its tags, in their order
      Given a card linked to "urgent" and "bug"
      When I read the cards
      Then the card reads with the tags "bug" and "urgent"

    Scenario: A card without tags reads with an empty list
      Given a card without tags
      When I read the cards
      Then the card reads with no tags

    Scenario: Every card's tags are read in one more command
      Given three cards, each linked to "docs"
      When I read the cards
      Then two commands are sent for the cards and their tags

    Scenario: Cards are kept by their tags, as F# reads a list
      Given a card linked to "urgent" and "bug"
      And a card linked to "docs" and "urgent"
      When I keep the cards where List.exists (fun t -> t.Name = "bug") over their tags
      Then only the card linked to "bug" is returned

    Scenario: Cards whose every tag passes, as F# reads a list
      Given a card linked to "urgent" and "bug"
      And a card linked to "docs" and "urgent"
      When I keep the cards where List.forall (fun t -> t.Name <> "bug") over their tags
      Then only the card linked to "docs" is returned

  Rule: Writing the owner writes only its links

    Scenario: Inserting a card links it to its tags
      When I insert a card linked to "urgent" and "bug"
      Then card_tags links the card to "urgent" and "bug"
      And the tags table still holds three tags

    Scenario: Changing the tags of a card read writes only its links
      Given a card linked to "urgent" and "bug"
      When I read it, then link it to "bug" and "docs" instead
      Then card_tags links the card to "bug" and "docs"
      And the tags table still holds three tags

    Scenario: A link to a tag that does not exist is the field's error
      When I insert a card linked to a tag that was never stored
      Then the insert fails with an error on Tags, naming card_tags_tag_id_fkey

    Scenario: Changing a linked tag through the card is refused
      Given a card linked to "urgent" and "bug"
      When I read it, then rename its tag "bug" to "defect" through the card
      Then the change is refused for Tags, saying a linked record belongs to its own aggregate
      And the tag "bug" keeps its name

    Scenario: Deleting a card deletes its links, not its tags
      Given a card linked to "urgent" and "bug"
      When I delete the card I read
      Then card_tags is empty
      And the tags table still holds three tags

  Rule: The schema check knows the join table

    Scenario: A join table without its foreign key is reported
      Given card_tags has lost its foreign key on tag_id
      When I check the cards and tags model against the database
      Then the join table card_tags is reported as missing its reference from tag_id to tags
