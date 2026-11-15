# Lesson 16 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About links and owned records
  An article links to shared tags through a join table: its Tags are read with it, and writing the article writes only its links. An account owns at most one profile, its own row, read and written with it as an option.

  Prerequisites: lesson 09, lesson 12.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                                | the learner discovers                                                                                      | teaches                                                                      |
      | An article is read with its tags                    | an article's Tags, shared records reached through article_tags, are read with it in the order of their key | linked-collections: Reading a card reads its tags, in their order            |
      | No tags is an empty list                            | an article linking to no tag reads with []                                                                 | linked-collections: A card without tags reads with an empty list             |
      | Every article's tags come in one more command       | the tags of every article come in one more command, through the join table                                 | linked-collections: Every card's tags are read in one more command           |
      | Filtering articles by their tags                    | List.exists over a.Tags runs in the database and keeps each article once                                   | linked-collections: Cards are kept by their tags, as F# reads a list         |
      | Changing the tags writes only links                 | an update with a new list of tags rewrites the article's links and writes no Tag                           | linked-collections: Changing the tags of a card read writes only its links   |
      | Adding and removing one tag                         | the list is the whole set of links: filtering it and appending to it changes only those links              | linked-collections: Changing the tags of a card read writes only its links   |
      | A link to a missing tag is the field's error        | a link to a tag that does not exist is the Tags field's ForeignKeyConstraint error                         | linked-collections: A link to a tag that does not exist is the field's error |
      | A tag is changed as a tag                           | renaming a linked tag through the article is refused as a Tags error: a tag is its own aggregate           | linked-collections: Changing a linked tag through the card is refused        |
      | Deleting an article deletes its links, not its tags | deleting an article deletes its rows of article_tags and leaves every tag                                  | linked-collections: Deleting a card deletes its links, not its tags          |
      | An account's profile is an option                   | an Account owns at most one Profile, read with it as Some profile                                          | owned-records: Reading an account reads its profile and settings             |
      | An account without a profile                        | an account with no profile reads with None                                                                 | owned-records: An account without a profile reads with None                  |
      | Adding a profile inserts it                         | changing Profile from None to Some inserts the profile row                                                 | owned-records: Adding the optional record inserts it                         |
      | Removing the profile deletes it                     | changing Profile from Some to None deletes the profile row                                                 | owned-records: Removing the optional record deletes it                       |
      | Filtering through an owned record                   | an exists over tableOf<Profile> keeps the accounts whose profile matches                                        | query-exists: Orders with a succeeded payment                                |
