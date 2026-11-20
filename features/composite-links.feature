# Executable acceptance specification (Reqnroll): linked lists between records
# keyed by several parts. Written before the implementation.
@implemented
Feature: A linked list joins records keyed by several parts
  As an F# application developer whose tables are keyed by more than one column
  I want a linked list between such records, as between records of one-part keys
  So that a many-to-many table over composite keys maps without a surrogate id

  A linked list's join table holds a column for each part of each side's key.
  For a key of one part that is the column it has always been, `<record>_id`;
  for a key of several parts it is `<record>_<part>` for each part, and a
  column name the JoinTable attribute gives for that side is the prefix. The
  join table's primary key is every column, and each side's columns reference
  that side's table. A linked list of records keyed by several parts is read
  in the order of the key's parts, since a whole key has no order. Links are
  written, read, queried through and checked as for keys of one part.

  # ThreadKey = { Repo: string; Number: int }   LabelKey = { Repo: string; Name: string }
  # Label = { Id: LabelKey; Colour: string }     Watcher = { Id: WatcherId; Login: string }
  # Thread = { Id: ThreadKey; Title: string; Labels: Label list (thread_labels); Watchers: Watcher list (thread_watchers) }
  # Card = { Id: CardId; Title: string; Labels: Label list (card_labels) }
  Background:
    Given a configured relational database
    And threads and cards linked to labels and watchers, keyed by several parts

  Rule: Links are written and read

    Scenario: A Thread is stored with its links, a column per part of each key
      Given the labels plasm/bug and plasm/docs, and the watcher ada
      When I insert the Thread plasm#7 "Crash" labelled plasm/bug and plasm/docs, watched by ada
      Then thread_labels holds plasm#7 to plasm/bug and plasm#7 to plasm/docs
      And thread_watchers holds plasm#7 to ada

    Scenario: A Thread is read back with its links, labels in the order of their key's parts
      Given the labels plasm/docs and plasm/bug, and the watcher ada
      And the Thread plasm#7 "Crash" labelled plasm/docs and plasm/bug, watched by ada
      When I read the Threads
      Then the Thread plasm#7 is labelled plasm/bug and plasm/docs, watched by ada

    Scenario: Relabelling a Thread replaces its links
      Given the labels plasm/bug and plasm/docs, and the watcher ada
      And the Thread plasm#7 "Crash" labelled plasm/bug, watched by ada
      When the Thread plasm#7 is relabelled plasm/docs
      Then thread_labels holds plasm#7 to plasm/docs

    Scenario: A Card of a one-part key links labels of several parts
      Given the labels plasm/bug and plasm/docs, and the watcher ada
      When I insert a linked Card "Release" labelled plasm/docs
      Then reading the linked Card back gives the labels plasm/docs

    Scenario: A query reads through the links
      Given the labels plasm/bug and plasm/docs, and the watcher ada
      And the Thread plasm#7 "Crash" labelled plasm/bug, watched by ada
      And the Thread plasm#8 "Docs" labelled plasm/docs, watched by ada
      When I read the Threads labelled with the name "bug"
      Then only the Thread plasm#7 is read

    Scenario: A link to a label that does not exist is the field's error
      Given the labels plasm/bug and plasm/docs, and the watcher ada
      When I insert the Thread plasm#9 "Stray" labelled plasm/missing, watched by ada
      Then the Thread is refused with a reference error on Labels, naming thread_labels_label_repo_label_name_fkey

  Rule: The join tables are described and checked

    Scenario: The DDL keys a join table by every part of both keys
      When I print the DDL for Threads linked to labels
      Then it declares "primary key (thread_repo, thread_number, label_repo, label_name)"
      And it declares "constraint thread_labels_thread_repo_thread_number_fkey foreign key (thread_repo, thread_number) references threads (repo, number)"
      And it declares "constraint thread_labels_label_repo_label_name_fkey foreign key (label_repo, label_name) references labels (repo, name)"

    Scenario: A join table missing a column of a key is reported
      Given the join table thread_labels has lost its column label_name
      When I check the linked threads against the database
      Then the missing column label_name of thread_labels is reported for Thread.Labels
