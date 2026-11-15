# Executable acceptance specification (Reqnroll): a single record an aggregate
# owns. Written before the implementation.
@implemented
Feature: An owned record is a plain field, loaded and written with its owner
  As an F# application developer
  I want my domain record to hold a single record it owns, optional or required
  So that a has-one relationship is a plain field, with nothing left unloaded

  `ownsOptional (fun a -> a.Profile) (fun p -> p.Account)` declares a record
  that may be absent (`Profile: Profile option`), and `ownsOne` one that must
  exist (`Settings: Settings`). The owned record is
  its own row, holding the owner's key. Like an owned list, it is always loaded
  with its owner and written with it.

  # Account = { Id: AccountId; Name: string; Profile: Profile option; Settings: Settings }
  # Profile = { Id: ProfileId; Account: AccountId; Bio: string }
  # Settings = { Id: SettingsId; Account: AccountId; Theme: string }
  Background:
    Given a configured relational database
    And accounts, profiles and settings tables

  Rule: An owned record is always loaded

    Scenario: Reading an account reads its profile and settings
      Given an Account "ada" with the bio "maths" and the theme "dark"
      When I read the Accounts
      Then "ada" reads with the bio "maths" and the theme "dark"

    Scenario: An account without a profile reads with None
      Given an Account "bob" without a profile and with the theme "light"
      When I read the Accounts
      Then "bob" reads without a profile

    Scenario: Every account's records are read in one more command each
      Given Accounts "ada", "bob" and "cy", each with a profile
      When I read the Accounts
      Then three commands are sent

    Scenario: An account whose required record is missing is refused when read
      Given an Account "ada" with the bio "maths" and the theme "dark"
      And its settings row is deleted behind Plasm's back
      When I read the Accounts
      Then the read is refused, naming the field Settings

  Rule: Writing the owner writes what it owns

    Scenario: Inserting an account inserts its records
      When I insert an Account "ada" with the bio "maths" and the theme "dark"
      Then the profiles table holds "maths" and the settings table holds "dark" for that Account
      And the Account returned holds its records with their keys

    Scenario: Changing the owned record of an account read updates it in place
      Given an Account "ada" with the bio "maths" and the theme "dark"
      When I read it, then change the bio to "logic" and the theme to "light"
      Then the profiles table holds "logic" and the settings table holds "light" for that Account
      And the profile kept its key

    Scenario: Removing the optional record deletes it
      Given an Account "ada" with the bio "maths" and the theme "dark"
      When I read it, then remove its profile
      Then the profiles table is empty

    Scenario: Adding the optional record inserts it
      Given an Account "bob" without a profile and with the theme "light"
      When I read it, then give it the bio "poetry"
      Then the profiles table holds "poetry" for that Account

    Scenario: Deleting an account deletes its records
      Given an Account "ada" with the bio "maths" and the theme "dark"
      When I delete the Account I read
      Then the profiles table and the settings table are empty

  Rule: Other queries reach an owned record

    These were has-one.feature's scenarios, before has-one became an owned
    record (one style, 2026-11-14).

    Scenario: Filtering through an owned record
      Given an Account "ada" with the bio "maths" and the theme "dark"
      And an Account "bob" with the bio "poetry" and the theme "light"
      When I keep the Accounts whose profile's bio is "maths", through exists
      Then only "ada" is read

    Scenario: Filtering through an owned record, as F# reads an option
      Given an Account "ada" with the bio "maths" and the theme "dark"
      And an Account "bob" with the bio "poetry" and the theme "light"
      When I keep the Accounts where Option.exists (fun p -> p.Bio = "maths") over their profile
      Then only "ada" is read

    Scenario: Accounts without an owned record
      Given an Account "ada" with the bio "maths" and the theme "dark"
      And an Account "bob" without a profile and with the theme "light"
      When I keep the Accounts whose profile is None
      Then only "bob" is read

    Scenario: An owned record matched by two rows is refused when read
      Given an Account "ada" with the bio "maths" and the theme "dark"
      And a second profile row for it, behind Plasm's back
      When I read the Accounts
      Then the read is refused, naming the field Profile

  Rule: A record field is owned or refused

    Scenario: A stored record field that does not hold the owner's key is refused at build, saying how to own it
      When I build a model of Accounts whose Settings do not hold the account's key
      Then the model is refused for Settings, saying how to make it owned
