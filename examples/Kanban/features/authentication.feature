Feature: People sign in, and changes are made in their name

  Anyone may read a board. Changing one needs an account, and the person a
  change is recorded for is the person who is signed in, never a name the
  request supplies.

  Background:
    Given the board "apollo" allows 2 cards in progress
    And nobody is signed in

  Rule: An account is a name and a password

    Scenario: Registering signs the person in
      When someone registers as "Margaret" with the password "moonshot-1969"
      Then "Margaret" is signed in

    Scenario: A name belongs to one account
      Given the account "Margaret" with the password "moonshot-1969"
      When someone registers as "Margaret" with the password "another-password"
      Then the request is invalid for the field "name": "is already taken"
      And nobody is signed in any more

    Scenario: A password has at least 8 characters
      When someone registers as "Margaret" with the password "short"
      Then the request is invalid for the field "password": "must be at least 8 characters"
      And nobody is signed in any more

    Scenario: The password itself is not stored
      Given the account "Margaret" with the password "moonshot-1969"
      Then no stored account contains the text "moonshot-1969"

  Rule: Signing in needs the account's password

    Scenario: The right password signs the person in
      Given the account "Margaret" with the password "moonshot-1969"
      When someone signs in as "Margaret" with the password "moonshot-1969"
      Then "Margaret" is signed in

    Scenario: A wrong password and an unknown name get the same answer
      Given the account "Margaret" with the password "moonshot-1969"
      When someone signs in as "Margaret" with the password "moonshot-1970"
      Then the name or the password is wrong
      When someone signs in as "Nobody" with the password "moonshot-1969"
      Then the name or the password is wrong
      And nobody is signed in any more

    Scenario: Signing out
      Given "Margaret" is signed in with an account
      When they sign out
      Then nobody is signed in any more

  Rule: Reading is open, changing is not

    Scenario: Anyone may read a board
      When the board "apollo" is read
      Then the request succeeds

    Scenario: Adding a card needs someone signed in
      When a card "Anonymous" is added to "apollo"
      Then the request is refused until someone signs in
      And the board "apollo" has the cards ""

    Scenario: Creating a board needs someone signed in
      When a board "gemini" named "Gemini" is created allowing 2 cards in progress
      Then the request is refused until someone signs in

    Scenario: Asking for one's work needs someone signed in
      When they ask what they are working on
      Then the request is refused until someone signs in

  Rule: A change is made in the name of whoever is signed in

    Scenario: The person who starts a card is the person signed in
      Given "Margaret" is signed in with an account
      And the card "Write the spec" on "apollo"
      When they start "Write the spec"
      Then the card "Write the spec" is in progress for Margaret

    Scenario: The author of a comment is the person signed in
      Given "Margaret" is signed in with an account
      And the card "Write the spec" on "apollo"
      When they comment "Started an outline" on "Write the spec"
      Then the card "Write the spec" has the comments "Margaret: Started an outline"
