# Lesson 10 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About constructors and constraints
  Plasm validates no input. What a value may be is the domain's: its constructors (create) refuse bad input, and Plasm reads stored values back through them. What the data may be is the database's: a constraint with PostgreSQL's default name comes back as that field's error, and any other as a database failure.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                         | the learner discovers                                                                                         | teaches                                                                             | hint                                                                                                                                            |
      | A constructor refuses bad input              | Plasm validates no input: Email.create, the domain's constructor, refuses an address without an '@'           | elegance: Domain reconstruction can fail explicitly                                 | Email.create in Shop.fs, under Given, shows the text of its Error with the input put in, and a good Email's Value is the text it was made from. |
      | What the domain refuses is refused when read | an Email is read back through Email.create, so a stored address it refuses is a read error naming Email       | scalar-conventions: A type with a create function is read through it                | Property is the name of the Customer field that could not be read, and Message is what Email.create in Shop.fs says about the stored text.      |
      | A duplicate is the field's error             | a duplicate email comes back as the Email field's error, by the constraint's default name customers_email_key | constraints: A duplicate of a unique field is the field's error                     | The comment names the field, the constraint and the kinds a field error can have. |
      | A missing reference is the field's error     | an order for a customer that does not exist is the CustomerId field's error                                   | constraints: A reference to a missing row is the field's error                      | The comment names the field that refers to a customer and lists Plasm's wordings, and the koan's name says which constraint broke. |
      | A broken check is the field's error          | a negative total breaks orders_total_check, and comes back as the Total field's CheckConstraint               | constraints: A broken column check is the field's error                             | The comment names the constraint, and the kinds are listed in the lesson's first constraint koan. |
      | Refused changes are never written            | a refused insert writes nothing                                                                               | core: Invalid changes are not persisted                                             | The insert breaks the same check as in the previous koan, and the comment says what a refused insert writes to the orders table.                |
      | A rule across columns is no field's error    | a constraint without a field's default name, orders_cancelled_has_reason, stays a database failure naming it  | constraints: A constraint whose name follows no convention stays a database failure | The comment gives the constraint's name, and the failure carries it as an option, since not every failure has a constraint.                     |
