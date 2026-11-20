# Executable acceptance specification (Reqnroll): a typed id among a union
# case's fields, or a value's, is a reference, as a record field of that type
# is. Written before the implementation.
@implemented
Feature: An id inside a union case or a value is a reference
  As an F# application developer whose unions and values name other records
  I want an id in a case or a value held by a foreign key, as an id in a field is
  So that the database refuses one naming a record that does not exist

  A typed id among a case's fields references the record whose key it is,
  stored in the case's column. The DDL declares the foreign key under the name
  Plasm maps (<table>_<column>_fkey), the schema check reports it missing or
  misnamed, and a violation comes back as the union field's error. The column
  is NULL for every case without that field, which a foreign key allows. Cases
  that share a field of one name share its column, and so one reference.

  A typed id among a value's fields (a record with no table, stored in its
  owner's row) is a reference the same way, named by its path (`Home.City`).
  Its column is NULL when the value or the field is optional and absent.

  # Member = { Id: MemberId; Name: string }
  # ShelfStatus = Free | OnLoan of borrower: MemberId | Held of borrower: MemberId * until: DateTimeOffset
  # Shelf = { Id: ShelfId; Label: string; Status: ShelfStatus }
  # City = { Id: CityId; Name: string }
  # Address = { Street: string; City: CityId; Near: CityId option }   (a value)
  # Person = { Id: PersonId; Home: Address; Work: Address option }
  Background:
    Given a configured relational database

  Rule: An id in a union case

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

  Rule: An id in a value

    Scenario: The model infers the reference from the value's field
      When I list the references the model of people infers
      Then Person.Home.City, Person.Home.Near, Person.Work.City and Person.Work.Near reference City, each once

    Scenario: The DDL declares a value's foreign key, by the name Plasm maps
      When I print the DDL for People with addresses
      Then it declares "constraint people_home_city_fkey foreign key (home_city) references cities (id)"
      And it declares "constraint people_work_near_fkey foreign key (work_near) references cities (id)"

    Scenario: An absent value, or an absent id, stores NULL under the foreign key
      Given the people tables as Plasm's DDL creates them, with a city
      When I insert a Person at home in that city, near no city, with no work address
      Then the Person is stored, with NULL in home_near, work_city and work_near

    Scenario: A city that does not exist is the value field's error
      Given the people tables as Plasm's DDL creates them, with a city
      When I insert a Person at home in the city with id 999
      Then the Person is refused with a reference error on Home, naming people_home_city_fkey

    Scenario: A missing foreign key in a value is reported by the schema check
      Given the people tables as Plasm's DDL creates them, without the foreign key on home_city
      When I check the people against the database
      Then the missing reference from Person's Home.City to cities is reported
