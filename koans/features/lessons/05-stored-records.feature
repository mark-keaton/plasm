# Lesson 05 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About stored records
  Domain types are ordinary immutable F# records and unions. A stored record names its table with [<Table>], and everything else follows from its types: its key is its field named Id, a column is named after its field, and ids, validated values and fieldless unions are stored by convention, with no mappings. A model is built once from the record types, before any query runs, and refuses what the types leave unclear.

  Prerequisites: lesson 04.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                  | the learner discovers                                                                                         | teaches                                                                        |
      | A stored record is still plain F#     | [<Table>] is a note on an immutable record: copy-and-update works as always                                   | elegance: Persistence metadata does not require mutable records                |
      | A record names its table              | [<Table "products">] says which table holds the records, and repo.Explain shows it                           | elegance: Persistence metadata does not require mutable records                |
      | Columns are named in snake_case       | a field called PlacedAt is stored in the column placed_at, by rule                                            | elegance: Persistence metadata does not require mutable records                |
      | A key finds one record                | a stored record's key is its field named Id; filtering on it finds exactly one Product                        | elegance: Domain scalar wrappers preserve their meaning                        |
      | A union is stored as text             | by convention a fieldless union is stored as its case name in snake_case: 'cancelled' reads back as Cancelled | scalar-conventions: A fieldless union is stored as its case name in snake case |
      | Wrapped keys keep their meaning       | by convention a single-case union such as CustomerId is stored as the number it wraps, and comes back wrapped | scalar-conventions: A single-case union is stored as the type it wraps         |
      | Stored values can be rejected         | a Quantity is read back through Quantity.create, so a stored zero is refused with the field named             | scalar-conventions: A type with a create function is read through it           |
      | Absent values are options             | a nullable column is an option field; None is not an empty string                                             | core: Nullable values remain explicit                                          |
      | A model reports every mistake at once | Model.build lists every field it cannot store, not just the first                                             | core: Unsupported model representations fail during configuration              |
      | A list is stored only if its records are | a list of records that name no table is refused when the model is built, naming the field                | stored-records: A model the types cannot describe is refused                   |
      | One record without a table lives in its owner's row | a record with no table held as one field is stored in its owner's row, as <field>_<subfield> columns | value-records: Ddl prints a value's columns and the CHECK for an optional one |
