# Executable acceptance specification (Reqnroll): has-one relationships.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit: hasOne existed but was untested.
@implemented
Feature: Has-one relationships
  As an F# application developer
  I want a record that owns at most one related record to load it like any relationship
  So that one-to-one data such as profiles needs no special handling

  A has-one relationship is declared on the owner, with the foreign key on the
  related record. Its field is Assoc<'T option>: None when there is no related record.

  Background:
    Given a configured relational database
    And mappings for Accounts and their Profiles

  Rule: Has-one relationships load like other relationships

    Scenario: Preloading a has-one relationship
      Given Accounts with and without a Profile
      When I query Accounts and preload their Profile
      Then each Account with a Profile has Some of it
      And each Account without a Profile has None
      And the Profiles are loaded with one batched command

    Scenario: Filtering through a has-one relationship
      Given Accounts with and without a Profile
      When I keep only Accounts whose Profile matches a condition
      Then only those Accounts are returned
      And the condition runs in the database

    Scenario: A has-one relationship with more than one related row is an error
      Given an Account with two Profiles
      When I query Accounts and preload their Profile
      Then the read fails with a materialisation error naming the relationship and the Account
