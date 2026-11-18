Feature: People sign in to the host, and changes are made in their name
  The host (examples/Kanban/src/Host) keeps who is signed in in a cookie.
  Anyone may read; a change needs someone signed in, and the person it is
  made for is the person signed in, never a name the request gives. The
  rules about names and passwords are the domain's (Person, Password); the
  host hashes the password and keeps the cookie. Each scenario runs the host
  as its own process, on a database of its own, over HTTP (docs/kanban-host.md).

  Background:
    Given the host serves a migrated database
    And board "alpha" has the cards:
      | title    | stage   | assignee | version | created              |
      | Build it | backlog |          | 1       | 2026-11-18T09:00:00Z |

  Rule: An account is a name and a password

    Scenario: Registering signs the person in
      When someone registers as "Margaret" with the password "moonshot-1969"
      Then the answer is 200
      And "Margaret" is signed in

    Scenario: A password has at least 8 characters
      When someone registers as "Margaret" with the password "short"
      Then the answer is 400 for the field "password": "must be at least 8 characters"
      And nobody is signed in

    Scenario: A name belongs to one account
      Given the account "Margaret" with the password "moonshot-1969"
      When someone registers as "Margaret" with the password "another-password"
      Then the answer is 400 for the field "name": "is already taken"
      And nobody is signed in

    Scenario: The password itself is not stored
      Given the account "Margaret" with the password "moonshot-1969"
      Then no stored account contains the text "moonshot-1969"

  Rule: Signing in needs the right password, and signing out ends it

    Scenario: The right password signs the person in
      Given the account "Margaret" with the password "moonshot-1969"
      When "Margaret" signs in with the password "moonshot-1969"
      Then the answer is 200
      And "Margaret" is signed in

    Scenario: A wrong password is refused
      Given the account "Margaret" with the password "moonshot-1969"
      When "Margaret" signs in with the password "moonshot-1970"
      Then the answer is 401
      And nobody is signed in

    Scenario: A name with no account is refused the same way
      When "Grace" signs in with the password "moonshot-1969"
      Then the answer is 401
      And nobody is signed in

    Scenario: Signing out ends it
      Given "Margaret" is signed in with the password "moonshot-1969"
      When the person signs out
      Then nobody is signed in

  Rule: A change is made by the person signed in

    Scenario: A change needs someone signed in
      When nobody signed in starts "Build it"
      Then the answer is 401
      And "Build it" is in the backlog

    Scenario: Starting a card records the person signed in, not a name the request gives
      Given "Margaret" is signed in with the password "moonshot-1969"
      When the person signed in starts "Build it", the request also naming "Grace"
      Then the answer is 200
      And "Build it" is in progress for "Margaret"
