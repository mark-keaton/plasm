# Executable acceptance specification (Reqnroll): a typed id among a union
# case's fields is a reference, as a record field of that type is. Written
# before the implementation.
@implemented
Feature: An id inside a union case is a reference
  As an F# application developer whose union's cases name other records
  I want an id in a case held by a foreign key, as an id in a field is
  So that the database refuses a case naming a record that does not exist

  A typed id among a case's fields references the record whose key it is,
  stored in the case's column. The DDL declares the foreign key under the name
  Plasm maps (<table>_<column>_fkey), the schema check reports it missing or
  misnamed, and a violation comes back as the union field's error. The column
  is NULL for every case without that field, which a foreign key allows. Cases
  that share a field of one name share its column, and so one reference.

  # Member = { Id: MemberId; Name: string }
  # ShelfStatus = Free | OnLoan of borrower: MemberId | Held of borrower: MemberId * until: DateTimeOffset
  # Shelf = { Id: ShelfId; Label: string; Status: ShelfStatus }
  Background:
    Given a configured relational database

  Scenario: The model infers the reference from the case field
    When I list the references the model of shelves infers
    Then Shelf.Status.borrower references Member, once

  Scenario: The DDL declares its foreign key, by the name Plasm maps
    When I print the DDL for Shelves lent to members
    Then it declares "constraint shelves_status_borrower_fkey foreign key (status_borrower) references members (id)"

  Scenario: A case that names no member stores NULL under the foreign key
    Given the shelves tables as Plasm's DDL creates them
    When I insert a free Shelf
    Then the Shelf is stored, with no borrower

  Scenario: A borrower that does not exist is the union field's error
    Given the shelves tables as Plasm's DDL creates them
    When I insert a Shelf on loan to the member with id 999
    Then the Shelf is refused with a reference error on Status, naming shelves_status_borrower_fkey

  Scenario: A missing foreign key is reported by the schema check
    Given the shelves tables as Plasm's DDL creates them, without the foreign key on status_borrower
    When I check the shelves against the database
    Then the missing reference from Shelf's Status.borrower to members is reported
