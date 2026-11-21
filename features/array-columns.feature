# Executable acceptance specification (Reqnroll): array columns. Written before
# the implementation.
@implemented
Feature: A list of scalars is stored as one array column
  As an F# application developer
  I want a list of plain values kept in its owner's row
  So that labels, logins and the like need neither a table nor a document

  A field whose type is a list of a scalar Plasm stores in one column is stored
  as one PostgreSQL array column of that scalar's type, by convention from the
  type: no attribute says so. That covers the built-in types, single-case
  wrappers, types read through their create function, and fieldless unions.
  What the list holds tells it apart: a list of stored records is owned or
  linked, as before; a list of scalars is an array; a list of anything else (a
  value, a union with fields) is refused, saying [<Json>] stores it as one
  document. [<Json>] on a list of scalars still chooses one jsonb document
  instead (json-columns.feature). The list is read and written whole, never NULL
  (an empty list is an empty array), and every element is read as its column
  would be, so a create function refusing an element is a read error naming the
  field and the element. Queries test an array with List.contains (= ANY) and
  List.isEmpty (cardinality); anything else over it is refused by name.

  # Issue = { Id: IssueId; Title: string; Labels: string list; Assignees: Login list; Points: int list; Topics: Topic list; Kinds: Kind list }
  # Login = Login of string          (a single-case wrapper)
  # Topic: a private representation, read through Topic.create (lowercase, 1 to 20 characters)
  # Kind = Bug | Feature             (a fieldless union, stored as its name)
  Background:
    Given a configured relational database
    And issues with labels, assignees, points, topics and kinds

  Rule: A list of scalars is one array column

    Scenario: An Issue is stored with its labels in one array
      When I insert an Issue "Crash" labelled "bug" and "ui", assigned to "ada" and "grace", with the points 3 and 5, the topics "core", and the kinds Bug
      Then one command is sent, inserting into gh_issues
      And the labels column holds {bug,ui}
      And the assignees column holds {ada,grace}
      And the points column holds {3,5}
      And the kinds column holds {bug}

    Scenario: An Issue is read back whole
      Given an Issue "Crash" labelled "bug" and "ui", assigned to "ada" and "grace", with the points 3 and 5, the topics "core", and the kinds Bug
      When I read the Issues
      Then the Issue is read as it was inserted

    Scenario: An empty list is an empty array, not NULL
      Given an Issue "Quiet" with nothing in its lists
      Then the labels column holds {}
      And the Issue "Quiet" is read with every list empty

    Scenario: An element its create function refuses is a read error naming the field and the element
      Given an Issue "Crash" labelled "bug"
      And the topics column of "Crash" is set behind Plasm's back to {core,BAD}
      When I read the Issues
      Then the read fails, saying Topics[1] and "must be lowercase"

    Scenario: A NULL array is a read error
      Given an Issue "Crash" labelled "bug"
      And the labels column of "Crash" is set behind Plasm's back to NULL
      When I read the Issues
      Then the read fails, saying Labels and "NULL"

    Scenario: A NULL element is a read error
      Given an Issue "Crash" labelled "bug"
      And the labels column of "Crash" is set behind Plasm's back to {bug,NULL}
      When I read the Issues
      Then the read fails, saying Labels[1] and "NULL"

    Scenario: Selecting an array field reads the list
      Given an Issue "Crash" labelled "bug" and "ui"
      When I select the labels of the Issues
      Then the labels selected are "bug" and "ui"

  Rule: A change writes the array whole

    Scenario: Relabelling an Issue writes the labels column and no other
      Given an Issue "Crash" labelled "bug"
      When "Crash" is relabelled "bug" and "regression"
      Then the update sets the labels column and no other
      And "Crash" is read labelled "bug" and "regression"

  Rule: A query tests an array with List.contains and List.isEmpty

    Scenario: Filtering by a value in an array
      Given an Issue "Crash" labelled "bug" and "ui"
      And an Issue "Docs" labelled "docs"
      When I read the Issues labelled "bug"
      Then the Issues read are "Crash"
      And on PostgreSQL the query's condition is = ANY("i"."labels")
      And on SQLite the query's condition is IN (SELECT value FROM json_each("i"."labels"))

    Scenario: Filtering by a wrapped value in an array
      Given an Issue "Crash" assigned to "ada"
      And an Issue "Docs" assigned to "grace"
      When I read the Issues assigned to "grace"
      Then the Issues read are "Docs"

    Scenario: Filtering by a prepared argument in an array
      Given an Issue "Crash" labelled "bug" and "ui"
      And an Issue "Docs" labelled "docs"
      When I read the Issues labelled "docs", through a prepared query
      Then the Issues read are "Docs"

    Scenario: Filtering by an empty array
      Given an Issue "Crash" labelled "bug"
      And an Issue "Quiet" with nothing in its lists
      When I read the Issues with no labels
      Then the Issues read are "Quiet"
      And on PostgreSQL the query's condition is cardinality("i"."labels") = 0
      And on SQLite the query's condition is json_array_length("i"."labels") = 0

    Scenario: Filtering by an array that is not empty
      Given an Issue "Crash" labelled "bug"
      And an Issue "Quiet" with nothing in its lists
      When I read the Issues with some label
      Then the Issues read are "Crash"

    Scenario Outline: Anything else over an array is refused, naming the field
      When I read the Issues <where>
      Then the read is refused, saying Issue.Labels and "<message>"

      Examples:
        | where                                      | message                           |
        | whose labels are exactly "bug"             | List.contains or List.isEmpty     |
        | with more than one label                   | List.contains or List.isEmpty     |
        | with a label starting with "b"             | List.contains or List.isEmpty     |
        | ordered by their labels                    | cannot be ordered by              |

  Rule: Every scalar a list holds is stored in its column's form

    Each element is held in the form a column of its type holds it, so a list is
    read back as it was written and List.contains finds what is there: on
    SQLite a JSON array of each scalar's own text (a Guid in capitals, a time
    to the tick it was given, a decimal keeping its scale) or exact number (an
    int64 past 2^53, a double's shortest text).

    Scenario: Every scalar a list can hold is read back as it was written
      Given a sample holding every scalar a list can hold, at the values that test its form
      Then the sample is read back as it was written

    Scenario: Every scalar a list holds is found by List.contains
      Given a sample holding every scalar a list can hold, at the values that test its form
      Then each of its values is found in its list by List.contains, the decimal by value

    @sqlite
    Scenario: A double that is not a number cannot be stored in a list on SQLite
      When a sample with NaN among its doubles is written
      Then the write of the sample is refused as an invalid change of Sample.Doubles, saying "JSON, which has no NaN or infinity"

    @sqlite
    Scenario: A double that is not a number cannot be stored in its own column on SQLite
      When a sample whose ratio is NaN is written
      Then the write of the sample is refused as an invalid change of Sample.Ratio, saying "SQLite holds no NaN"

    @sqlite
    Scenario: A query among doubles one of which is not a number is refused on SQLite
      When I read the samples whose ratio is among 1.5 and NaN
      Then the read is refused as a query that cannot be translated, saying "no NaN"

    @sqlite
    Scenario: A list column holding what is not a JSON array is a read error naming the column
      Given a sample holding every scalar a list can hold, at the values that test its form
      And the sample's bigs column is set behind Plasm's back to "a string"
      When I read the samples
      Then the read of the samples fails, saying "bigs" and "not a list of Int64"

    @sqlite
    Scenario: A list element of the wrong type is a read error naming the column and the element
      Given a sample holding every scalar a list can hold, at the values that test its form
      And the sample's bigs column is set behind Plasm's back to [1,"two"]
      When I read the samples
      Then the read of the samples fails, saying "bigs" and "element 1"

  Rule: The database's tables are described and checked

    @postgres
    Scenario: Ddl prints array columns
      When I print the DDL of the issues model
      Then it has the columns labels text[] not null, assignees text[] not null, points integer[] not null, topics text[] not null and kinds text[] not null

    @sqlite
    Scenario: Ddl prints array columns on SQLite
      When I print the DDL of the issues model
      Then it has the columns labels TEXT not null, assignees TEXT not null, points TEXT not null, topics TEXT not null and kinds TEXT not null
      And each list column is held to a JSON array by its check

    @postgres
    Scenario Outline: The schema check finds an array column of the wrong type
      Given the <column> column is <type>
      When I check the issues model against the database
      Then an incompatible type is reported for Issue.<field>, naming <needed>

      Examples:
        | column | type     | field  | needed    |
        | labels | text     | Labels | text[]    |
        | points | bigint[] | Points | integer[] |

    @sqlite
    Scenario Outline: The schema check finds an array column of the wrong type on SQLite
      Given the <column> column is declared <type>
      When I check the issues model against the database
      Then an incompatible type is reported for Issue.<field>, naming <needed>

      Examples:
        | column | type    | field  | needed |
        | labels | INTEGER | Labels | TEXT   |
        | points | REAL    | Points | TEXT   |

    Scenario: The schema check finds an array column that may hold NULL
      Given the labels column may hold NULL
      When I check the issues model against the database
      Then a nullable column is reported for Issue.Labels

    Scenario: The schema check accepts the issues' array columns
      When I check the issues model against the database
      Then no schema issue is reported

  Rule: What cannot be an array column is refused when the model is built

    Scenario Outline: <records> is refused
      When I build an array model of <records>
      Then the model is refused for <model>, saying "<message>"

      Examples:
        | records                    | model         | message                                       |
        | a list of optional strings | OptionalItems | an element of an array cannot be absent       |
        | an optional list           | OptionalList  | an empty list is already no elements          |
        | an array field             | ArrayField    | use a list                                    |
        | a list of values           | ValueList     | mark it [<Json>] to store it as one document  |
        | a list of unions of fields | UnionList     | mark it [<Json>] to store it as one document  |
        | a list of a PostgreSQL enum | EnumList     | is a Stage list: an array of a type stored as stage is not supported |
