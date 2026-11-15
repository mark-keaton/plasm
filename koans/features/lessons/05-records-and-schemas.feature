# Lesson 05 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About records and schemas
  Domain types are ordinary immutable F# records and unions. How they are stored lives in separate schema values; ids, validated values and fieldless unions are stored by convention, with no mappings; and a model is validated once, before any query runs.

  Prerequisites: lesson 04.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                  | the learner discovers                                                                                         | teaches                                                                        |
      | A record needs nothing from Plasm     | persistence metadata lives in a schema value, not on the record                                               | elegance: Persistence metadata does not require mutable records                |
      | A schema names the table              | a schema value says which table holds the records, and repo.Explain shows it                                  | elegance: Persistence metadata does not require mutable records                |
      | Columns are named in snake_case       | a field called PlacedAt is stored in the column placed_at unless the schema says otherwise                    | elegance: Persistence metadata does not require mutable records                |
      | A key finds one record                | the schema's key is the Id field; filtering on it finds exactly one Product                                   | elegance: Domain scalar wrappers preserve their meaning                        |
      | A union is stored as text             | by convention a fieldless union is stored as its case name in snake_case: 'cancelled' reads back as Cancelled | scalar-conventions: A fieldless union is stored as its case name in snake case |
      | Wrapped keys keep their meaning       | by convention a single-case union such as CustomerId is stored as the number it wraps, and comes back wrapped | scalar-conventions: A single-case union is stored as the type it wraps         |
      | Stored values can be rejected         | a Quantity is read back through Quantity.create, so a stored zero is refused with the field named             | scalar-conventions: A type with a create function is read through it           |
      | Absent values are options             | a nullable column is an option field; None is not an empty string                                             | core: Nullable values remain explicit                                          |
      | A model reports every mistake at once | Model.buildSchemas lists every field it cannot store, not just the first                                             | core: Unsupported model representations fail during configuration              |
